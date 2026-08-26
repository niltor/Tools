using WindowsCleanup.Localization;

namespace WindowsCleanup.Cleanup;

public enum CleanupLocationKind
{
	DirectoryContents,
	MatchingFiles,
	RecycleBin
}

public enum CleanupRisk
{
	Safe,
	Moderate
}

public sealed record CleanupLocation(
	string Path,
	CleanupLocationKind Kind = CleanupLocationKind.DirectoryContents,
	string? Pattern = null,
	bool Recursive = true,
	int? MinimumAgeDays = null);

public sealed record CleanupSnapshot(int FileCount, long Bytes, int LocationsWithFiles)
{
	public static CleanupSnapshot Empty { get; } = new(0, 0, 0);
}

public sealed class CleanupTarget
{
	public CleanupTarget(
		string id,
		LocalizedText category,
		LocalizedText name,
		LocalizedText note,
		IEnumerable<CleanupLocation> locations,
		bool requiresAdministrator = false,
		CleanupRisk risk = CleanupRisk.Safe,
		bool optIn = false)
	{
		Id = id;
		Category = category;
		Name = name;
		Note = note;
		Locations = locations
			.Where(location => !string.IsNullOrWhiteSpace(location.Path))
			.GroupBy(location => $"{location.Kind}|{location.Path}|{location.Pattern}|{location.Recursive}|{location.MinimumAgeDays}", StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.ToArray();
		RequiresAdministrator = requiresAdministrator;
		Risk = risk;
		OptIn = optIn;
	}

	public string Id { get; }

	public LocalizedText Category { get; }

	public LocalizedText Name { get; }

	public LocalizedText Note { get; }

	public IReadOnlyList<CleanupLocation> Locations { get; }

	public bool RequiresAdministrator { get; }

	public CleanupRisk Risk { get; }

	public bool OptIn { get; }

	public CleanupSnapshot Snapshot { get; internal set; } = CleanupSnapshot.Empty;
}

public sealed record CleanupExecutionResult(
	int DeletedFiles,
	long DeletedBytes,
	int FailedFiles,
	IReadOnlyList<string> Errors)
{
	public static CleanupExecutionResult Empty { get; } = new(0, 0, 0, Array.Empty<string>());
}
