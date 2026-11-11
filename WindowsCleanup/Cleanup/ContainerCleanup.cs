using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace WindowsCleanup.Cleanup;

internal sealed record ContainerCandidate(
	string Id,
	string Label,
	string Size,
	bool IsVolume,
	bool Untagged,
	long? Bytes = null,
	bool BytesEstimated = false,
	string? ComposeProject = null,
	bool? Anonymous = null);

internal sealed record ContainerStorageUsage(long? Images, long? Volumes);

internal sealed class ContainerCleanup(string command)
{
	public string Command => command;

	public async Task<string[]> GetLocalConnectionAsync()
	{
		if (command == "docker")
		{
			var endpoint = Environment.GetEnvironmentVariable("DOCKER_HOST");
			if (string.IsNullOrWhiteSpace(endpoint) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_CONTEXT")))
				endpoint = (await RunAsync([], "context", "inspect", "--format", "{{.Endpoints.docker.Host}}")).Trim();
			if (endpoint.StartsWith("npipe:////./pipe/", StringComparison.OrdinalIgnoreCase))
				return ["--host", endpoint];
		}
		else
		{
			var rows = Lines(await RunAsync([], "system", "connection", "list", "--format", "{{.URI}}\t{{.Identity}}\t{{.Default}}"));
			foreach (var row in rows.Select(line => line.Split('\t')))
			{
				if (row.Length == 3 && row[2] == "true" && Uri.TryCreate(row[0], UriKind.Absolute, out var uri) &&
					uri.Scheme == "ssh" && uri.IsLoopback && File.Exists(row[1]))
					return ["--url", row[0], "--identity", row[1]];
			}
		}
		throw new InvalidOperationException("No supported local container connection / 未检测到受支持的本地容器连接。");
	}

	public async Task<IReadOnlyList<ContainerCandidate>> ScanAsync(string[] connection, bool measureSpace = true, bool review = false)
	{
		var containers = Lines(await RunAsync(connection, "ps", "--all", "--quiet", "--no-trunc"));
		var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var batch in containers.Chunk(50))
		{
			if (batch.Any(id => !IsImageId(id))) throw new InvalidOperationException("Unexpected container ID.");
			var ids = Lines(await RunAsync(connection, ["container", "inspect", "--format", "{{.Image}}", ..batch]));
			if (ids.Length != batch.Length || ids.Any(id => !IsImageId(id)))
				throw new InvalidOperationException("Unable to verify all container image references.");
			referenced.UnionWith(ids.Select(NormalizeId));
		}

		var imageRows = Lines(await RunAsync(connection, "image", "ls", "--no-trunc", "--format", "{{.ID}}\t{{.Repository}}\t{{.Tag}}\t{{.Size}}"));
		var dangling = review ? null : Lines(await RunAsync(connection, "image", "ls", "--filter", "dangling=true", "--no-trunc", "--quiet"))
			.Where(IsImageId).Select(NormalizeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var candidates = imageRows.Select(line => line.Split('\t'))
			.Where(row => row.Length == 4 && IsImageId(row[0]) && !referenced.Contains(NormalizeId(row[0])))
			.GroupBy(row => NormalizeId(row[0]), StringComparer.OrdinalIgnoreCase)
			.Select(group => new ContainerCandidate(group.Key, string.Join(", ", group.Select(row => row[1] + ":" + row[2]).Distinct()),
				group.First()[3], false, group.All(row => row[2] == "<none>")))
			.Where(item => review || (item.Untagged && dangling!.Contains(item.Id))).ToList();

		var volumes = review
			? Lines(await RunAsync(connection, "volume", "ls", "--filter", "dangling=true", "--format", "{{.Name}}\t{{.Driver}}"))
			: [];
		var volumeIds = volumes.Select(line => line.Split('\t'))
			.Where(row => row.Length == 2 && Regex.IsMatch(row[0], @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]*\z") && row[1] == "local")
			.Select(row => row[0]).Distinct(StringComparer.Ordinal);
		foreach (var batch in volumeIds.Chunk(50))
		{
			string[] details;
			try
			{
				// JSON preserves volume labels and Podman's Anonymous field. Keep a
				// template fallback for older engines that do not support {{json .}}.
				details = Lines(await RunAsync(connection, ["volume", "inspect", "--format", "{{json .}}", ..batch]));
			}
			catch (InvalidOperationException)
			{
				details = Lines(await RunAsync(connection, ["volume", "inspect", "--format", "{{.Name}}\t{{.Mountpoint}}", ..batch]));
			}
			foreach (var detail in details)
			{
				if (TryReadVolume(detail, batch, out var name, out var mountpoint, out var composeProject, out var anonymous))
					candidates.Add(new ContainerCandidate(name, name + " (local)\n" + mountpoint, "—", true, false,
						ComposeProject: composeProject, Anonymous: anonymous));
			}
		}
		if (measureSpace && candidates.Count > 0)
		{
			var fullIds = imageRows.Select(line => line.Split('\t')[0]).Where(IsImageId)
				.Select(NormalizeId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			ApplySpaceUsage(candidates, await GetSpaceUsageAsync(connection, fullIds));
		}
		return candidates;
	}

	public Task<string> DeleteAsync(string[] connection, ContainerCandidate item)
		=> item.IsVolume ? RunAsync(connection, "volume", "rm", item.Id)
			: RunAsync(connection, "image", "rm", "--no-prune", item.Id);

	public async Task<ContainerStorageUsage> MeasureStorageAsync(string[] connection)
	{
		long? images = null, volumes = null;
		try
		{
			var output = await RunAsync(connection, "system", "df", "--format", "{{.Type}}\t{{.Size}}");
			foreach (var line in Lines(output))
			{
				var cells = line.Split('\t', 2);
				if (cells.Length != 2) continue;
				if (cells[0] == "Images") images = ParseHumanBytes(cells[1]);
				else if (cells[0] == "Local Volumes") volumes = ParseHumanBytes(cells[1]);
			}
		}
		catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Win32Exception)
		{
			// Measurement is optional; it must not mask deletion results or invent a zero.
		}
		return new ContainerStorageUsage(images, volumes);
	}

	private static string[] Lines(string output) => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
	private static string NormalizeId(string id) => id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..] : id;
	private static bool IsImageId(string id) => Regex.IsMatch(NormalizeId(id), @"\A[0-9a-fA-F]{64}\z");

	private async Task<SpaceUsage> GetSpaceUsageAsync(string[] connection, string[] fullIds)
	{
		// Podman rejects --format combined with --verbose; its JSON only contains totals.
		if (command == "podman") return await GetVerboseSpaceUsageAsync(connection, fullIds);
		try
		{
			var json = await RunAsync(connection, "system", "df", "-v", "--format", "json");
			using var document = JsonDocument.Parse(json);
			var images = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
			var volumes = new Dictionary<string, long?>(StringComparer.Ordinal);
			if (document.RootElement.TryGetProperty("Images", out var imageArray))
			{
				foreach (var image in imageArray.EnumerateArray())
				{
					var id = image.TryGetProperty("ID", out var imageId) ? NormalizeId(imageId.GetString() ?? "") : "";
					if (IsImageId(id)) images[id] = ParseHumanBytes(ReadString(image, "UniqueSize"));
				}
			}
			if (document.RootElement.TryGetProperty("Volumes", out var volumeArray))
			{
				foreach (var volume in volumeArray.EnumerateArray())
				{
					var name = ReadString(volume, "Name");
					if (!string.IsNullOrWhiteSpace(name)) volumes[name] = ParseHumanBytes(ReadString(volume, "Size"));
				}
			}
			return new SpaceUsage(images, volumes);
		}
		catch (TimeoutException)
		{
			return SpaceUsage.Empty;
		}
		catch (Exception exception) when (exception is InvalidOperationException or JsonException or NotSupportedException)
		{
			return await GetVerboseSpaceUsageAsync(connection, fullIds);
		}
	}

	private async Task<SpaceUsage> GetVerboseSpaceUsageAsync(string[] connection, string[] fullIds)
	{
		string output;
		try { output = await RunAsync(connection, "system", "df", "-v"); }
		catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { return SpaceUsage.Empty; }

		var images = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
		var volumes = new Dictionary<string, long?>(StringComparer.Ordinal);
		var prefixes = fullIds.GroupBy(id => id[..12], StringComparer.OrdinalIgnoreCase)
			.Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
		var section = "";
		foreach (var line in Lines(output))
		{
			if (line.EndsWith("space usage:", StringComparison.OrdinalIgnoreCase))
			{
				section = line.Trim();
				continue;
			}
			var cells = Regex.Split(line.Trim(), @"\s+");
			if (section == "Images space usage:" && cells.Length >= 8 && prefixes.TryGetValue(cells[2], out var id) &&
				int.TryParse(cells[^1], out _))
				images[id] = ParseHumanBytes(cells[^2]);
			else if (section == "Local Volumes space usage:" && cells.Length == 3 && int.TryParse(cells[1], out _))
				volumes[cells[0]] = ParseHumanBytes(cells[2]);
		}
		return new SpaceUsage(images, volumes);
	}

	private static void ApplySpaceUsage(List<ContainerCandidate> candidates, SpaceUsage usage)
	{
		for (var index = 0; index < candidates.Count; index++)
		{
			var candidate = candidates[index];
			if (candidate.IsVolume)
			{
				if (usage.Volumes.TryGetValue(candidate.Id, out var volumeBytes))
					candidates[index] = candidate with { Bytes = volumeBytes, BytesEstimated = false };
			}
			else if (usage.Images.TryGetValue(NormalizeId(candidate.Id), out var imageBytes))
			{
				candidates[index] = candidate with { Bytes = imageBytes, BytesEstimated = true };
			}
		}
	}

	private static string ReadString(JsonElement element, string property)
		=> element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? "" : "";

	private static bool TryReadVolume(string detail, string[] batch, out string name, out string mountpoint,
		out string? composeProject, out bool? anonymous)
	{
		name = "";
		mountpoint = "";
		composeProject = null;
		anonymous = null;
		try
		{
			using var document = JsonDocument.Parse(detail);
			var volume = document.RootElement;
			name = ReadString(volume, "Name");
			mountpoint = ReadString(volume, "Mountpoint");
			if (string.IsNullOrWhiteSpace(name) || !batch.Contains(name, StringComparer.Ordinal)) return false;

			if (volume.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
			{
				composeProject = ReadLabel(labels, "com.docker.compose.project")
					?? ReadLabel(labels, "io.podman.compose.project");
				anonymous = ReadAnonymous(labels);
			}
			if (volume.TryGetProperty("Anonymous", out var anonymousValue))
			{
				if (anonymousValue.ValueKind == JsonValueKind.True || anonymousValue.ValueKind == JsonValueKind.False)
					anonymous = anonymousValue.GetBoolean();
				else if (anonymousValue.ValueKind == JsonValueKind.String && bool.TryParse(anonymousValue.GetString(), out var parsed))
					anonymous = parsed;
			}
			return true;
		}
		catch (JsonException)
		{
			var row = detail.Split('\t', 2);
			if (row.Length != 2 || !batch.Contains(row[0], StringComparer.Ordinal)) return false;
			name = row[0];
			mountpoint = row[1];
			return true;
		}
	}

	private static string? ReadLabel(JsonElement labels, string name)
		=> labels.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString() : null;

	private static bool? ReadAnonymous(JsonElement labels)
	{
		foreach (var labelName in new[] { "com.docker.volume.anonymous", "io.podman.volume.anonymous" })
		{
			if (!labels.TryGetProperty(labelName, out var value)) continue;
			if (value.ValueKind == JsonValueKind.True) return true;
			if (value.ValueKind == JsonValueKind.False) return false;
			if (value.ValueKind == JsonValueKind.String)
			{
				var text = value.GetString();
				if (bool.TryParse(text, out var parsed)) return parsed;
				// Docker marks anonymous volumes with an empty label value.
				return true;
			}
			return true;
		}
		return null;
	}

	private static long? ParseHumanBytes(string value)
	{
		if (string.IsNullOrWhiteSpace(value) || value is "N/A" or "—") return null;
		var match = Regex.Match(value.Trim(), @"\A(?<number>[0-9]+(?:\.[0-9]+)?)\s*(?<unit>B|kB|MB|GB|TB|KiB|MiB|GiB|TiB)\z");
		if (!match.Success || !double.TryParse(match.Groups["number"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)) return null;
		var unit = match.Groups["unit"].Value;
		var multiplier = unit switch
		{
			"KiB" => 1L << 10, "MiB" => 1L << 20, "GiB" => 1L << 30, "TiB" => 1L << 40,
			"kB" => 1_000L, "MB" => 1_000_000L, "GB" => 1_000_000_000L, "TB" => 1_000_000_000_000L, _ => 1L
		};
		return number >= long.MaxValue / multiplier ? long.MaxValue : (long)Math.Round(number * multiplier);
	}

	private sealed record SpaceUsage(IReadOnlyDictionary<string, long?> Images, IReadOnlyDictionary<string, long?> Volumes)
	{
		public static SpaceUsage Empty { get; } = new(new Dictionary<string, long?>(), new Dictionary<string, long?>());
	}

	private async Task<string> RunAsync(string[] connection, params string[] args)
	{
		var info = new ProcessStartInfo(command)
		{
			UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
		};
		foreach (var arg in connection.Concat(args)) info.ArgumentList.Add(arg);
		if (connection.Length > 0 && command == "docker") info.Environment.Remove("DOCKER_CONTEXT");
		using var process = Process.Start(info) ?? throw new Win32Exception("Cannot start " + command);
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		try { await process.WaitForExitAsync(timeout.Token); }
		catch (OperationCanceledException)
		{
			try { process.Kill(entireProcessTree: true); }
			catch (InvalidOperationException) { }
			throw new TimeoutException(command + " timed out / 命令超时。");
		}
		var output = await stdout;
		var errors = await stderr;
		if (process.ExitCode != 0) throw new InvalidOperationException(errors.Trim());
		return output;
	}
}
