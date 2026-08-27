namespace WindowsCleanup.Registry;

public sealed record InstalledApplication(
	string DisplayName,
	string? Publisher,
	string? DisplayVersion,
	string? InstallLocation,
	string? ExecutablePath,
	string? UninstallString,
	string Hive,
	string View,
	string KeyPath);
