using Spectre.Console;
using WindowsCleanup.Localization;

namespace WindowsCleanup.Migration;

public enum MigrationConfigurationKind
{
	KnownFolder,
	UserEnvironment,
	RegistryValue,
	PnpmStore
}

public sealed record MigrationConfiguration(
	MigrationConfigurationKind Kind,
	string? Name = null,
	string? RegistryPath = null,
	string? ValueName = null,
	Guid? KnownFolderId = null);

public sealed record MigrationTarget(
	string Id,
	LocalizedText Category,
	LocalizedText Name,
	string SourcePath,
	LocalizedText Plan,
	LocalizedText ConfigurationPlan,
	MigrationConfiguration Configuration,
	IReadOnlyList<string> ProcessNames)
{
	public long Bytes { get; init; }

	public override string ToString()
		=> Markup.Escape($"{Name.English} - {SourcePath} ({FormatBytes(Bytes)})");

	private static string FormatBytes(long bytes)
	{
		const double scale = 1024;
		if (bytes < scale)
		{
			return $"{bytes} B";
		}

		var units = new[] { "KB", "MB", "GB", "TB" };
		var unit = -1;
		double scaled = bytes;
		while (scaled >= scale && unit < units.Length - 1)
		{
			scaled /= scale;
			unit++;
		}

		return $"{scaled:0.##} {units[unit]}";
	}
}

public sealed record MigrationDrive(string RootPath, long FreeBytes)
{
	public override string ToString()
		=> Markup.Escape(RootPath);
}

public sealed record MigrationPlanItem(
	MigrationTarget Target,
	string SourcePath,
	string DestinationPath,
	long Bytes,
	int FileCount);

public sealed record MigrationExecutionResult(
	int MigratedTargets,
	long MigratedBytes,
	int FailedTargets,
	IReadOnlyList<string> Errors);
