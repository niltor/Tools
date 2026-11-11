using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace WindowsCleanup.Cleanup;

internal sealed record WorkspaceGroup(string Manifest, string Label, HashSet<string> Members);

internal static class WorkspaceGroups
{
	private static readonly string[] ManifestNames =
		["package.json", "pnpm-workspace.yaml", "pnpm-workspace.yml", "Cargo.toml", "go.work", "pyproject.toml", "pom.xml", "settings.gradle", "settings.gradle.kts"];

	public static IEnumerable<string> FindManifests(string directory)
	{
		foreach (var name in ManifestNames)
		{
			var path = Path.Combine(directory, name);
			if (File.Exists(path)) yield return path;
		}

		string[] solutions;
		try { solutions = Directory.GetFiles(directory, "*.sln*"); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
		foreach (var path in solutions)
			if (IsSolution(path)) yield return path;
	}

	public static WorkspaceGroup[] Discover(IEnumerable<string> manifests, string[] projects, CancellationToken token)
	{
		var groups = new List<WorkspaceGroup>();
		var manifestPaths = manifests.ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var manifest in manifestPaths)
		{
			token.ThrowIfCancellationRequested();
			if (SafeFileSystem.IsReparsePoint(manifest)) continue;
			try
			{
				var root = Path.GetDirectoryName(manifest)!;
				var (label, references) = Read(manifest);
				if (references.Length == 0) continue;
				if (label == "Maven") references = MavenMembers(manifest, references, manifestPaths, token);
				var literal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				if (!IsSolution(manifest)) literal.Add(root);
				var include = new List<Regex>();
				var exclude = new List<Regex>();
				foreach (var reference in references)
				{
					var negative = reference.StartsWith('!');
					var relative = (negative ? reference[1..] : reference).Replace('\\', '/').TrimEnd('/');
					if (relative.Length == 0) continue;
					if (!negative && !relative.Contains('*') && !relative.Contains('?'))
					{
						literal.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(relative, root)));
						continue;
					}
					if (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
					var expression = Regex.Escape(relative).Replace(@"\*\*/", "(?:.*/)?")
						.Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");
					(negative ? exclude : include).Add(new Regex("\\A" + expression + "\\z",
						RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
				}

				var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (var project in projects)
				{
					var relative = Path.GetRelativePath(root, project).Replace('\\', '/');
					var inside = relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
					if ((literal.Contains(project) || (inside && include.Any(rule => rule.IsMatch(relative)))) &&
						!exclude.Any(rule => rule.IsMatch(relative)) && IsMemberProject(label, project))
						members.Add(project);
				}
				if (members.Count > 0) groups.Add(new WorkspaceGroup(manifest, label, members));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or XmlException or RegexMatchTimeoutException or System.Text.Json.JsonException)
			{
				// Unreadable or malformed workspace files leave the project-level fallback available.
			}
		}

		return groups.OrderByDescending(group => group.Members.Count)
			.ThenByDescending(group => IsSolution(group.Manifest))
			.ThenBy(group => group.Manifest.Count(character => character is '\\' or '/'))
			.ThenBy(group => group.Manifest, StringComparer.OrdinalIgnoreCase).ToArray();
	}

	private static bool IsMemberProject(string label, string directory)
	{
		string? marker = label switch
		{
			"npm workspace" or "pnpm workspace" => "package.json",
			"Cargo" => "Cargo.toml",
			"Go workspace" => "go.mod",
			"uv" => "pyproject.toml",
			_ => null
		};
		return marker is null || File.Exists(Path.Combine(directory, marker));
	}

	private static string[] MavenMembers(string manifest, string[] references, HashSet<string> manifests, CancellationToken token)
	{
		var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { manifest };
		var pending = new Stack<(string Manifest, string[] References)>();
		pending.Push((manifest, references));
		while (pending.TryPop(out var current))
		{
			token.ThrowIfCancellationRequested();
			foreach (var reference in current.References)
			{
				var path = Path.GetFullPath(reference.Replace('\\', '/'), Path.GetDirectoryName(current.Manifest)!);
				var moduleManifest = path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? path : Path.Combine(path, "pom.xml");
				if (!manifests.Contains(moduleManifest) || SafeFileSystem.IsReparsePoint(moduleManifest)) continue;
				members.Add(Path.GetDirectoryName(moduleManifest)!);
				if (visited.Add(moduleManifest)) pending.Push((moduleManifest, Read(moduleManifest).References));
			}
		}
		return members.ToArray();
	}

	private static bool IsSolution(string path) => Path.GetExtension(path).Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
		Path.GetExtension(path).Equals(".slnx", StringComparison.OrdinalIgnoreCase);

	private static (string Label, string[] References) Read(string manifest)
	{
		var fileName = Path.GetFileName(manifest).ToLowerInvariant();
		var extension = Path.GetExtension(manifest).ToLowerInvariant();
		if (IsSolution(manifest))
		{
			var projectFiles = extension == ".slnx"
				? XDocument.Load(manifest).Descendants().Where(element => element.Name.LocalName == "Project")
					.Select(element => (string?)element.Attribute("Path"))
				: File.ReadLines(manifest).Select(line =>
				{
					var match = Regex.Match(line, """^\s*Project\("[^"]+"\)\s*=\s*"[^"]*",\s*"([^"]+)"\s*,""");
					return match.Success ? match.Groups[1].Value : null;
				});
			var root = Path.GetDirectoryName(manifest)!;
			var references = projectFiles.Where(path => !string.IsNullOrWhiteSpace(path) &&
					new[] { ".csproj", ".fsproj", ".vbproj", ".esproj", ".vcxproj" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
				.Select(path => Path.GetFullPath(path!.Replace('\\', '/'), root))
				.Where(File.Exists).Select(path => Path.GetDirectoryName(path)!).ToArray();
			return (".NET", references);
		}
		if (fileName == "pom.xml")
		{
			var document = XDocument.Load(manifest);
			var modules = document.Root?.Elements().Where(element => element.Name.LocalName == "modules")
				.SelectMany(element => element.Elements().Where(module => module.Name.LocalName == "module"))
				.Select(module => module.Value.Trim()).Where(value => value.Length > 0).ToArray() ?? [];
			return ("Maven", modules);
		}
		if (fileName is "settings.gradle" or "settings.gradle.kts")
		{
			var source = File.ReadAllText(manifest);
			if (source.Contains("projectDir", StringComparison.Ordinal)) return ("Gradle", []);
			var modules = Regex.Matches(source, """(?m)^\s*include\b\s*(?:\(([^)]*)\)|([^\r\n]+))""")
				.SelectMany(match => Regex.Matches(match.Value, """["'](:?[\w.-]+(?::[\w.-]+)*)["']"""))
				.Select(match => match.Groups[1].Value.TrimStart(':').Replace(':', '/')).ToArray();
			return ("Gradle", modules);
		}
		var entries = WorkspaceManifest.Read(manifest).ToArray();
		return (entries.FirstOrDefault().Label ?? "", entries.Select(entry => entry.Pattern).ToArray());
	}
}
