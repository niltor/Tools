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
	public override string ToString()
		=> Markup.Escape($"{Name.English} - {SourcePath}");
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
