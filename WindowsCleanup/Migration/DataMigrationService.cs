#pragma warning disable CA1416

using System.Diagnostics;
using WindowsCleanup.Cleanup;
using WindowsCleanup.Localization;

namespace WindowsCleanup.Migration;

internal static class DataMigrationService
{
	private const long FreeSpaceSafetyMargin = 16L * 1024 * 1024;

	public static IReadOnlyList<MigrationDrive> GetDestinationDrives()
	{
		if (!OperatingSystem.IsWindows())
		{
			return Array.Empty<MigrationDrive>();
		}

		var systemRoot = GetSystemDriveRoot();
		var drives = new List<MigrationDrive>();
		foreach (var drive in DriveInfo.GetDrives())
		{
			try
			{
				var root = NormalizeRoot(drive.RootDirectory.FullName);
				if (!drive.IsReady || drive.DriveType != DriveType.Fixed ||
					string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				drives.Add(new MigrationDrive(root, drive.AvailableFreeSpace));
			}
			catch (Exception exception) when (IsFileSystemException(exception))
			{
				// A drive that disappears or cannot report free space is not a target.
			}
		}

		return drives
			.OrderByDescending(drive => drive.FreeBytes)
			.ToArray();
	}

	public static IReadOnlyList<MigrationPlanItem> BuildPlan(
		IReadOnlyList<MigrationTarget> targets,
		string destinationDriveRoot)
	{
		if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(destinationDriveRoot))
		{
			return Array.Empty<MigrationPlanItem>();
		}

		var destinationRoot = NormalizeRoot(destinationDriveRoot);
		var systemRoot = GetSystemDriveRoot();
		if (string.IsNullOrWhiteSpace(systemRoot) ||
			string.Equals(destinationRoot, systemRoot, StringComparison.OrdinalIgnoreCase))
		{
			return Array.Empty<MigrationPlanItem>();
		}

		var plan = new List<MigrationPlanItem>();
		long plannedBytes = 0;
		foreach (var target in targets.DistinctBy(target => target.Id))
		{
			if (!TryGetDirectorySnapshot(target.SourcePath, out var snapshot, out _))
			{
				continue;
			}

			try
			{
				var sourcePath = Path.GetFullPath(target.SourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				var destinationPath = Path.Combine(destinationRoot, "WinClean", "Migration", target.Id);
				if (!IsOnRoot(sourcePath, systemRoot) || IsRootPath(sourcePath) ||
					!IsOnRoot(destinationPath, destinationRoot) ||
					Directory.Exists(destinationPath) && Directory.EnumerateFileSystemEntries(destinationPath).Any() ||
					plan.Any(existing => UserFolderDiscovery.IsSameOrDescendant(sourcePath, existing.SourcePath) ||
						UserFolderDiscovery.IsSameOrDescendant(existing.SourcePath, sourcePath)))
				{
					continue;
				}

				plannedBytes = checked(plannedBytes + snapshot.Bytes);
				plan.Add(new MigrationPlanItem(target, sourcePath, destinationPath, snapshot.Bytes, snapshot.FileCount));
			}
			catch (Exception exception) when (exception is OverflowException or ArgumentException or IOException or NotSupportedException)
			{
				// A malformed or changing path is omitted from the plan.
			}
		}

		if (plan.Count == 0 || !HasEnoughSpace(destinationRoot, plannedBytes))
		{
			return Array.Empty<MigrationPlanItem>();
		}

		return plan;
	}

	public static MigrationExecutionResult Execute(
		IReadOnlyList<MigrationPlanItem> plan,
		AppLanguage language)
	{
		var migratedTargets = 0;
		long migratedBytes = 0;
		var failedTargets = 0;
		var errors = new List<string>();

		foreach (var item in plan)
		{
			var targetName = item.Target.Name.Get(language);
			try
			{
				if (TryGetRunningProcess(item.Target.ProcessNames, out var processName))
				{
					failedTargets++;
					AddError(errors, $"{targetName}: process '{processName}' is running; close it and retry.");
					continue;
				}

				if (!IsConfigurationStillAtSource(item.Target.Configuration, item.SourcePath, out var configurationStateError))
				{
					failedTargets++;
					AddError(errors, $"{targetName}: configuration changed since scanning ({configurationStateError}); the source was kept.");
					continue;
				}

				if (!TryGetDirectorySnapshot(item.SourcePath, out var beforeCopy, out var snapshotError) ||
					beforeCopy.Bytes != item.Bytes || beforeCopy.FileCount != item.FileCount)
				{
					failedTargets++;
					AddError(errors, $"{targetName}: source changed or could not be read before copying ({snapshotError ?? "snapshot mismatch"}).");
					continue;
				}

				if (Directory.Exists(item.DestinationPath) && Directory.EnumerateFileSystemEntries(item.DestinationPath).Any())
				{
					failedTargets++;
					AddError(errors, $"{targetName}: destination already contains data: {item.DestinationPath}");
					continue;
				}

				var copy = CopyDirectory(item.SourcePath, item.DestinationPath);
				if (copy.FailedFiles > 0 || copy.Bytes != item.Bytes || copy.FileCount != item.FileCount)
				{
					failedTargets++;
					AddErrors(errors, copy.Errors);
					AddError(errors, $"{targetName}: copy verification failed; the source was kept.");
					continue;
				}

				if (!TryGetDirectorySnapshot(item.SourcePath, out var afterCopy, out var afterCopyError) ||
					afterCopy.Bytes != item.Bytes || afterCopy.FileCount != item.FileCount)
				{
					failedTargets++;
					AddError(errors, $"{targetName}: source changed while copying ({afterCopyError ?? "snapshot mismatch"}); configuration was not changed.");
					continue;
				}

				if (!TryApplyConfiguration(item.Target.Configuration, item.DestinationPath, out var configurationError))
				{
					failedTargets++;
					AddError(errors, $"{targetName}: configuration was not updated ({configurationError}). The copied data was kept at {item.DestinationPath}.");
					continue;
				}

				var deletion = DeleteSource(item.SourcePath);
				if (deletion.FailedFiles > 0)
				{
					failedTargets++;
					AddErrors(errors, deletion.Errors);
					AddError(errors, $"{targetName}: configuration was updated but some source files could not be removed. The destination was kept.");
					continue;
				}

				migratedTargets++;
				migratedBytes += copy.Bytes;
			}
			catch (Exception exception) when (IsFileSystemException(exception) || exception is InvalidOperationException)
			{
				failedTargets++;
				AddError(errors, $"{targetName}: {exception.Message}");
			}
		}

		return new MigrationExecutionResult(migratedTargets, migratedBytes, failedTargets, errors);
	}

	private static bool TryApplyConfiguration(
		MigrationConfiguration configuration,
		string destination,
		out string error)
	{
		error = string.Empty;
		try
		{
			switch (configuration.Kind)
			{
				case MigrationConfigurationKind.KnownFolder when configuration.KnownFolderId is Guid knownFolderId:
					return KnownFolderPathService.TrySetPath(knownFolderId, destination, out error);

				case MigrationConfigurationKind.UserEnvironment when !string.IsNullOrWhiteSpace(configuration.Name):
					Environment.SetEnvironmentVariable(configuration.Name, destination, EnvironmentVariableTarget.User);
					return true;

				case MigrationConfigurationKind.RegistryValue
					when !string.IsNullOrWhiteSpace(configuration.RegistryPath) && !string.IsNullOrWhiteSpace(configuration.ValueName):
					using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(configuration.RegistryPath, writable: true))
					{
						if (key is null)
						{
							error = "The application registry setting is not writable.";
							return false;
						}

						key.SetValue(configuration.ValueName, destination, Microsoft.Win32.RegistryValueKind.String);
					}
					return true;

				case MigrationConfigurationKind.PnpmStore:
					return TrySetPnpmStore(destination, out error);

				default:
					error = "The migration configuration is incomplete.";
					return false;
			}
		}
		catch (Exception exception) when (IsConfigurationException(exception))
		{
			error = exception.Message;
			return false;
		}
	}

	private static bool IsConfigurationStillAtSource(
		MigrationConfiguration configuration,
		string source,
		out string error)
	{
		error = string.Empty;
		try
		{
			switch (configuration.Kind)
			{
				case MigrationConfigurationKind.UserEnvironment when !string.IsNullOrWhiteSpace(configuration.Name):
				{
					var environmentValue = Environment.GetEnvironmentVariable(configuration.Name);
					if (string.IsNullOrWhiteSpace(environmentValue) || PathsEqual(environmentValue, source))
					{
						return true;
					}

					error = $"{configuration.Name} points to another directory";
					return false;
				}

				case MigrationConfigurationKind.KnownFolder when configuration.KnownFolderId is Guid knownFolderId:
				{
					using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
						@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
						writable: false);
					var configuredValues = KnownFolderPathService.GetRegistryValueNames(knownFolderId)
						.Select(valueName => key?.GetValue(valueName)?.ToString())
						.Where(value => !string.IsNullOrWhiteSpace(value))
						.ToArray();
					if (configuredValues.Length == 0 || configuredValues.All(value => PathsEqual(value!, source)))
					{
						return true;
					}

					error = "the known-folder registry value points to another directory";
					return false;
				}

				case MigrationConfigurationKind.RegistryValue
					when !string.IsNullOrWhiteSpace(configuration.RegistryPath) && !string.IsNullOrWhiteSpace(configuration.ValueName):
				{
					using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(configuration.RegistryPath, writable: false);
					var configured = key?.GetValue(configuration.ValueName)?.ToString();
					if (!string.IsNullOrWhiteSpace(configured) && PathsEqual(configured, source))
					{
						return true;
					}

					error = "the application registry value points to another directory";
					return false;
				}

				case MigrationConfigurationKind.PnpmStore:
					return true;

				default:
					error = "the migration configuration is incomplete";
					return false;
			}
		}
		catch (Exception exception) when (IsConfigurationException(exception))
		{
			error = exception.Message;
			return false;
		}
	}

	private static bool PathsEqual(string left, string right)
	{
		var leftPath = Environment.ExpandEnvironmentVariables(left.Trim().Trim('"'));
		var rightPath = Path.GetFullPath(right);
		return string.Equals(
			Path.GetFullPath(leftPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			rightPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			StringComparison.OrdinalIgnoreCase);
	}

	private static bool TrySetPnpmStore(string destination, out string error)
	{
		error = string.Empty;
		try
		{
			using var process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = "pnpm.cmd",
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
				}
			};
			process.StartInfo.ArgumentList.Add("config");
			process.StartInfo.ArgumentList.Add("set");
			process.StartInfo.ArgumentList.Add("store-dir");
			process.StartInfo.ArgumentList.Add(destination);
			process.StartInfo.ArgumentList.Add("--global");
			if (!process.Start())
			{
				error = "pnpm could not be started.";
				return false;
			}

			var standardOutput = process.StandardOutput.ReadToEndAsync();
			var standardError = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(15_000))
			{
				process.Kill(entireProcessTree: true);
				error = "pnpm configuration timed out.";
				return false;
			}

			var output = standardError.GetAwaiter().GetResult().Trim();
			_ = standardOutput.GetAwaiter().GetResult();
			if (process.ExitCode != 0)
			{
				error = string.IsNullOrWhiteSpace(output) ? $"pnpm exited with code {process.ExitCode}." : output;
				return false;
			}

			return true;
		}
		catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
		{
			error = exception.Message;
			return false;
		}
	}

	private static MigrationFileOperationResult CopyDirectory(string source, string destination)
	{
		var copiedFiles = 0;
		long copiedBytes = 0;
		var failedFiles = 0;
		var errors = new List<string>();
		var pending = new Stack<(string Source, string Destination)>();
		pending.Push((source, destination));

		while (pending.Count > 0)
		{
			var (currentSource, currentDestination) = pending.Pop();
			try
			{
				Directory.CreateDirectory(currentDestination);
				foreach (var entry in Directory.EnumerateFileSystemEntries(currentSource))
				{
					if (SafeFileSystem.IsReparsePoint(entry))
					{
						failedFiles++;
						AddError(errors, $"Skipped reparse point during copy: {entry}");
						continue;
					}

					var destinationEntry = Path.Combine(currentDestination, Path.GetFileName(entry));
					if (Directory.Exists(entry))
					{
						pending.Push((entry, destinationEntry));
						continue;
					}

					if (!File.Exists(entry))
					{
						continue;
					}

					var length = new FileInfo(entry).Length;
					File.Copy(entry, destinationEntry, overwrite: false);
					TryPreserveFileTimes(entry, destinationEntry);
					copiedFiles++;
					copiedBytes = checked(copiedBytes + length);
				}
			}
			catch (Exception exception) when (IsFileSystemException(exception) || exception is OverflowException)
			{
				failedFiles++;
				AddError(errors, $"{currentSource}: {exception.Message}");
			}
		}

		return new MigrationFileOperationResult(copiedFiles, copiedBytes, failedFiles, errors);
	}

	private static MigrationFileOperationResult DeleteSource(string source)
	{
		var deletedFiles = 0;
		long deletedBytes = 0;
		var failedFiles = 0;
		var errors = new List<string>();
		var directories = new List<string>();
		var pending = new Stack<string>();
		pending.Push(source);

		while (pending.Count > 0)
		{
			var current = pending.Pop();
			directories.Add(current);
			try
			{
				foreach (var entry in Directory.EnumerateFileSystemEntries(current))
				{
					if (SafeFileSystem.IsReparsePoint(entry))
					{
						failedFiles++;
						AddError(errors, $"Skipped reparse point during source cleanup: {entry}");
						continue;
					}

					if (Directory.Exists(entry))
					{
						pending.Push(entry);
						continue;
					}

					if (!File.Exists(entry))
					{
						continue;
					}

					var length = new FileInfo(entry).Length;
					File.Delete(entry);
					deletedFiles++;
					deletedBytes = checked(deletedBytes + length);
				}
			}
			catch (Exception exception) when (IsFileSystemException(exception) || exception is OverflowException)
			{
				failedFiles++;
				AddError(errors, $"{current}: {exception.Message}");
			}
		}

		foreach (var directory in directories.OrderByDescending(GetDepth))
		{
			try
			{
				if (!Directory.Exists(directory))
				{
					continue;
				}

				if (Directory.EnumerateFileSystemEntries(directory).Any())
				{
					failedFiles++;
					AddError(errors, $"Source directory is not empty: {directory}");
					continue;
				}

				Directory.Delete(directory, recursive: false);
			}
			catch (Exception exception) when (IsFileSystemException(exception))
			{
				failedFiles++;
				AddError(errors, $"{directory}: {exception.Message}");
			}
		}

		return new MigrationFileOperationResult(deletedFiles, deletedBytes, failedFiles, errors);
	}

	private static bool TryGetDirectorySnapshot(
		string path,
		out MigrationDirectorySnapshot snapshot,
		out string? error)
	{
		snapshot = new MigrationDirectorySnapshot(0, 0);
		error = null;
		try
		{
			if (!Directory.Exists(path) || SafeFileSystem.IsReparsePoint(path))
			{
				error = "source directory is missing or is a reparse point";
				return false;
			}

			var files = 0;
			long bytes = 0;
			var pending = new Stack<string>();
			pending.Push(path);
			while (pending.Count > 0)
			{
				var current = pending.Pop();
				foreach (var entry in Directory.EnumerateFileSystemEntries(current))
				{
					if (SafeFileSystem.IsReparsePoint(entry))
					{
						error = $"reparse point found: {entry}";
						return false;
					}

					if (Directory.Exists(entry))
					{
						pending.Push(entry);
						continue;
					}

					if (!File.Exists(entry))
					{
						continue;
					}

					files++;
					bytes = checked(bytes + new FileInfo(entry).Length);
				}
			}

			snapshot = new MigrationDirectorySnapshot(files, bytes);
			return true;
		}
		catch (Exception exception) when (IsFileSystemException(exception) || exception is OverflowException)
		{
			error = exception.Message;
			return false;
		}
	}

	private static void TryPreserveFileTimes(string source, string destination)
	{
		try
		{
			File.SetCreationTimeUtc(destination, File.GetCreationTimeUtc(source));
			File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
		}
		catch (Exception exception) when (IsFileSystemException(exception))
		{
			// Timestamps are cosmetic; the copied bytes remain valid if this fails.
		}
	}

	private static bool HasEnoughSpace(string destinationRoot, long bytes)
	{
		try
		{
			var available = new DriveInfo(destinationRoot).AvailableFreeSpace;
			return bytes <= available && available - bytes >= FreeSpaceSafetyMargin;
		}
		catch (Exception exception) when (IsFileSystemException(exception))
		{
			return false;
		}
	}

	private static bool TryGetRunningProcess(IReadOnlyList<string> processNames, out string processName)
	{
		foreach (var name in processNames.Where(name => !string.IsNullOrWhiteSpace(name)))
		{
			try
			{
				if (Process.GetProcessesByName(name).Length > 0)
				{
					processName = name;
					return true;
				}
			}
			catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
			{
				processName = name;
				return true;
			}
		}

		processName = string.Empty;
		return false;
	}

	private static string? GetSystemDriveRoot()
	{
		try
		{
			var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
			if (string.IsNullOrWhiteSpace(windows))
			{
				return null;
			}

			var root = Path.GetPathRoot(Path.GetFullPath(windows));
			return string.IsNullOrWhiteSpace(root) ? null : NormalizeRoot(root);
		}
		catch (Exception exception) when (IsFileSystemException(exception))
		{
			return null;
		}
	}

	private static string NormalizeRoot(string path)
		=> Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

	private static bool IsOnRoot(string path, string root)
	{
		var fullPath = Path.GetFullPath(path);
		var normalizedRoot = NormalizeRoot(root);
		return fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsRootPath(string path)
	{
		var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase);
	}

	private static int GetDepth(string path)
		=> path.Count(character => character is '\\' or '/');

	private static void AddErrors(ICollection<string> errors, IEnumerable<string> newErrors)
	{
		foreach (var error in newErrors)
		{
			if (errors.Count >= 10)
			{
				break;
			}

			errors.Add(error);
		}
	}

	private static void AddError(ICollection<string> errors, string error)
	{
		if (errors.Count < 10)
		{
			errors.Add(error);
		}
	}

	private static bool IsFileSystemException(Exception exception)
		=> exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException;

	private static bool IsConfigurationException(Exception exception)
		=> IsFileSystemException(exception) || exception is InvalidOperationException or System.ComponentModel.Win32Exception;

	private sealed record MigrationDirectorySnapshot(int FileCount, long Bytes);
	private sealed record MigrationFileOperationResult(int FileCount, long Bytes, int FailedFiles, IReadOnlyList<string> Errors);
}

#pragma warning restore CA1416
