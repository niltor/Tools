using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsCleanup.Cleanup;

/// <summary>Reads conservative workspace membership declarations without executing project tools.</summary>
internal static class WorkspaceManifest
{
	public static IEnumerable<(string Label, string Pattern)> Read(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return [];
		var results = new List<(string Label, string Pattern)>();
		switch (Path.GetFileName(path).ToLowerInvariant())
		{
			case "package.json":
				ReadPackageJson(path, results);
				break;
			case "pnpm-workspace.yaml":
			case "pnpm-workspace.yml":
				ReadYaml(path, results);
				break;
			case "cargo.toml":
				ReadTomlMembers(path, "workspace", results, "Cargo");
				break;
			case "pyproject.toml":
				ReadTomlMembers(path, "tool.uv.workspace", results, "uv");
				break;
			case "go.work":
				ReadGoWork(path, results);
				break;
		}
		return results
			.Select(item => (item.Label, NormalizePattern(item.Pattern)))
			.Where(item => !string.IsNullOrWhiteSpace(item.Item2))
			.Distinct()
			.ToArray();
	}

	private static void ReadPackageJson(string file, ICollection<(string Label, string Pattern)> results)
	{
		try
		{
			if (!File.Exists(file)) return;
			using var document = JsonDocument.Parse(File.ReadAllText(file));
			if (!document.RootElement.TryGetProperty("workspaces", out var workspaces)) return;
			if (workspaces.ValueKind == JsonValueKind.Array)
			{
				foreach (var item in workspaces.EnumerateArray())
					if (item.ValueKind == JsonValueKind.String) results.Add(("npm workspace", item.GetString()!));
			}
			else if (workspaces.ValueKind == JsonValueKind.Object && workspaces.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Array)
			{
				foreach (var item in packages.EnumerateArray())
					if (item.ValueKind == JsonValueKind.String) results.Add(("npm workspace", item.GetString()!));
			}
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
		{
			// A malformed manifest must not broaden discovery.
		}
	}

	private static void ReadTomlMembers(string file, string section, ICollection<(string Label, string Pattern)> results, string label)
	{
		try
		{
			if (!File.Exists(file)) return;
			var active = false;
			string? pendingKey = null;
			foreach (var raw in File.ReadLines(file))
			{
				var line = raw.Split('#')[0].Trim();
				if (line.StartsWith("[", StringComparison.Ordinal))
				{
					active = line.Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase);
					pendingKey = null;
					continue;
				}
				if (!active) continue;
				if (pendingKey is null)
				{
					if (line.StartsWith("members", StringComparison.OrdinalIgnoreCase)) pendingKey = "members";
					else if (line.StartsWith("exclude", StringComparison.OrdinalIgnoreCase)) pendingKey = "exclude";
					else continue;
				}
				foreach (var value in QuotedValues(line)) results.Add((label, pendingKey == "exclude" ? "!" + value : value));
				if (line.Contains(']')) pendingKey = null;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
	}

	private static void ReadGoWork(string file, ICollection<(string Label, string Pattern)> results)
	{
		try
		{
			if (!File.Exists(file)) return;
			var inUse = false;
			foreach (var raw in File.ReadLines(file))
			{
				var line = raw.Split('#')[0].Trim();
				if (line == "use (") { inUse = true; continue; }
				if (inUse && line == ")") { inUse = false; continue; }
				if ((inUse || line.StartsWith("use ", StringComparison.Ordinal)) && !line.StartsWith("use (") && line != ")")
					results.Add(("Go workspace", line.StartsWith("use ", StringComparison.Ordinal) ? line[4..].Trim() : line));
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
	}

	private static void ReadYaml(string file, ICollection<(string Label, string Pattern)> results)
	{
		try
		{
			if (!File.Exists(file)) return;
			var inPackages = false;
			foreach (var raw in File.ReadLines(file))
			{
				var line = raw.Split('#')[0].Trim();
				if (line.Equals("packages:", StringComparison.OrdinalIgnoreCase)) { inPackages = true; continue; }
				if (inPackages && line.EndsWith(":", StringComparison.Ordinal) && !line.StartsWith("-", StringComparison.Ordinal)) { inPackages = false; continue; }
				if (!inPackages || !line.StartsWith("-", StringComparison.Ordinal)) continue;
				var value = line[1..].Trim().Trim('"', '\'');
				if (value.Length > 0) results.Add(("pnpm workspace", value));
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
	}

	private static IEnumerable<string> QuotedValues(string line)
		=> Regex.Matches(line, "[\\\"'](?<value>[^\\\"']+)[\\\"']")
			.Select(match => match.Groups["value"].Value);

	private static string NormalizePattern(string value)
	{
		var pattern = value.Trim().Trim('"', '\'').Replace('\\', '/');
		return pattern.StartsWith('!') ? "!" + pattern[1..].TrimStart('/') : pattern.TrimStart('/');
	}
}
