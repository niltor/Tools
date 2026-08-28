using System.Diagnostics;
using Spectre.Console;

namespace WindowsCleanup.Cleanup;

public sealed record LargeFileCandidate(string Path, long Bytes)
{
	public override string ToString() => Markup.Escape($"{Path} ({FormatBytes(Bytes)})");

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

public sealed record LargeFileScanProgress(
	string CurrentPath,
	long ScannedDirectories,
	long ScannedFiles,
	long CandidateCount,
	long SkippedEntries);

public sealed record LargeFileDeletionResult(
	int DeletedFiles,
	long DeletedBytes,
	int SkippedFiles,
	int FailedFiles,
	IReadOnlyList<string> Errors);

public static class LargeFileScanner
{
	public const long MinimumBytes = 100L * 1024 * 1024;
	public const int MaximumDisplayedFiles = 50;

	private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);
	private static readonly string[] ExcludedDirectoryNames =
	{
		"$Recycle.Bin",
		"$WINDOWS.~BT",
		"$Windows.~WS",
		"Boot",
		"Config.Msi",
		"Documents and Settings",
		"EFI",
		"MSOCache",
		"PerfLogs",
		"ProgramData",
		"Program Files",
		"Program Files (x86)",
		"Recovery",
		"System Volume Information",
		"Windows",
		"Windows.old"
	};

	private static readonly HashSet<string> SystemRootFileNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"bootmgr",
		"BOOTNXT",
		"DumpStack.log",
		"DumpStack.log.tmp",
		"hiberfil.sys",
		"pagefile.sys",
		"swapfile.sys"
	};

	public static IReadOnlyList<LargeFileCandidate> Find(
		Action<LargeFileScanProgress>? onProgress = null,
		CancellationToken cancellationToken = default)
	{
		if (!OperatingSystem.IsWindows())
		{
			return Array.Empty<LargeFileCandidate>();
		}

		var systemRoot = GetSystemDriveRoot();
		return systemRoot is null
			? Array.Empty<LargeFileCandidate>()
			: Find(systemRoot, onProgress, cancellationToken);
	}

	internal static IReadOnlyList<LargeFileCandidate> Find(
		string systemRoot,
		Action<LargeFileScanProgress>? onProgress = null,
		CancellationToken cancellationToken = default)
	{
		var root = NormalizePath(systemRoot);
		if (root is null || !Directory.Exists(root))
		{
			return Array.Empty<LargeFileCandidate>();
		}

		var excludedRoots = BuildExcludedRoots(root);
		var candidates = new List<LargeFileCandidate>(MaximumDisplayedFiles);
		var pending = new Stack<string>();
		pending.Push(root);

		long scannedDirectories = 0;
		long scannedFiles = 0;
		long candidateCount = 0;
		long skippedEntries = 0;
		var lastProgressTicks = Stopwatch.GetTimestamp();

		void ReportProgress(string currentPath, bool force = false)
		{
			if (onProgress is null || (!force && Stopwatch.GetElapsedTime(lastProgressTicks) < ProgressInterval))
			{
				return;
			}

			onProgress(new LargeFileScanProgress(
				currentPath,
				scannedDirectories,
				scannedFiles,
				candidateCount,
				skippedEntries));
			lastProgressTicks = Stopwatch.GetTimestamp();
		}

		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var current = pending.Pop();
			scannedDirectories++;
			ReportProgress(current);

			try
			{
				foreach (var entry in Directory.EnumerateFileSystemEntries(current))
				{
					cancellationToken.ThrowIfCancellationRequested();
					ReportProgress(current);
					if (SafeFileSystem.IsReparsePoint(entry))
					{
						skippedEntries++;
						continue;
					}

					bool isDirectory;
					try
					{
						isDirectory = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
					}
					catch (Exception exception) when (IsFileSystemException(exception))
					{
						skippedEntries++;
						continue;
					}

					if (isDirectory)
					{
						if (!IsExcludedPath(entry, excludedRoots))
						{
							pending.Push(entry);
						}

						continue;
					}

					scannedFiles++;
					if (IsSystemRootFile(entry, root) ||
						!SafeFileSystem.TryGetLength(entry, out var length))
					{
						if (!IsSystemRootFile(entry, root))
						{
							skippedEntries++;
						}

						continue;
					}

					if (length <= MinimumBytes)
					{
						continue;
					}

					candidateCount++;
					AddTopCandidate(candidates, new LargeFileCandidate(entry, length));
					ReportProgress(current);
				}
			}
			catch (Exception exception) when (IsFileSystemException(exception))
			{
				skippedEntries++;
			}

			ReportProgress(current);
		}

		ReportProgress(root, force: true);
		return candidates
			.OrderByDescending(candidate => candidate.Bytes)
			.ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	public static LargeFileDeletionResult Delete(IEnumerable<LargeFileCandidate> candidates)
	{
		var systemRoot = GetSystemDriveRoot();
		if (systemRoot is null)
		{
			return new LargeFileDeletionResult(0, 0, 0, 1, new[] { "The Windows system drive could not be determined." });
		}

		var excludedRoots = BuildExcludedRoots(systemRoot);
		var deletedFiles = 0;
		long deletedBytes = 0;
		var skippedFiles = 0;
		var failedFiles = 0;
		var errors = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var candidate in candidates)
		{
			try
			{
				var path = NormalizePath(candidate.Path);
				if (path is null ||
					!UserFolderDiscovery.IsSameOrDescendant(path, systemRoot) ||
					IsExcludedPath(path, excludedRoots) ||
					IsSystemRootFile(path, systemRoot) ||
					!seen.Add(path) ||
					!File.Exists(path) ||
					SafeFileSystem.IsReparsePoint(path) ||
					!SafeFileSystem.TryGetLength(path, out var length) ||
					length <= MinimumBytes ||
					length != candidate.Bytes)
				{
					skippedFiles++;
					continue;
				}

				File.Delete(path);
				deletedFiles++;
				deletedBytes = checked(deletedBytes + length);
			}
			catch (Exception exception) when (IsFileSystemException(exception) || exception is OverflowException)
			{
				failedFiles++;
				if (errors.Count < 5)
				{
					errors.Add($"{candidate.Path}: {exception.Message}");
				}
			}
		}

		return new LargeFileDeletionResult(deletedFiles, deletedBytes, skippedFiles, failedFiles, errors);
	}

	private static void AddTopCandidate(List<LargeFileCandidate> candidates, LargeFileCandidate candidate)
	{
		if (candidates.Count < MaximumDisplayedFiles)
		{
			candidates.Add(candidate);
			return;
		}

		var smallest = candidates
			.Select((item, index) => (item, index))
			.OrderBy(pair => pair.item.Bytes)
			.ThenByDescending(pair => pair.item.Path, StringComparer.OrdinalIgnoreCase)
			.First();
		if (candidate.Bytes > smallest.item.Bytes ||
			candidate.Bytes == smallest.item.Bytes && string.Compare(candidate.Path, smallest.item.Path, StringComparison.OrdinalIgnoreCase) < 0)
		{
			candidates[smallest.index] = candidate;
		}
	}

	private static IReadOnlyList<string> BuildExcludedRoots(string systemRoot)
	{
		var paths = new List<string>();
		foreach (var directoryName in ExcludedDirectoryNames)
		{
			AddExcludedRoot(paths, Path.Combine(systemRoot, directoryName), systemRoot);
		}

		foreach (var specialFolder in new[]
		{
			Environment.SpecialFolder.Windows,
			Environment.SpecialFolder.ProgramFiles,
			Environment.SpecialFolder.ProgramFilesX86,
			Environment.SpecialFolder.CommonApplicationData
		})
		{
			AddExcludedRoot(paths, Environment.GetFolderPath(specialFolder), systemRoot);
		}

		return paths
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static void AddExcludedRoot(ICollection<string> paths, string? path, string systemRoot)
	{
		var fullPath = NormalizePath(path);
		if (fullPath is not null &&
			!string.Equals(fullPath, systemRoot, StringComparison.OrdinalIgnoreCase) &&
			UserFolderDiscovery.IsSameOrDescendant(fullPath, systemRoot))
		{
			paths.Add(fullPath);
		}
	}

	private static bool IsExcludedPath(string path, IReadOnlyList<string> excludedRoots)
		=> excludedRoots.Any(root => UserFolderDiscovery.IsSameOrDescendant(path, root));

	private static bool IsSystemRootFile(string path, string systemRoot)
	{
		try
		{
			var parent = Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var root = systemRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) &&
				SystemRootFileNames.Contains(Path.GetFileName(path));
		}
		catch (Exception exception) when (IsFileSystemException(exception))
		{
			return true;
		}
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

			return NormalizePath(Path.GetPathRoot(Path.GetFullPath(windows)));
		}
		catch (Exception exception) when (IsFileSystemException(exception))
		{
			return null;
		}
	}

	private static string? NormalizePath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}

		try
		{
			var fullPath = Path.GetFullPath(path);
			var pathRoot = Path.GetPathRoot(fullPath);
			return string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase)
				? pathRoot
				: fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}
		catch (Exception exception) when (IsFileSystemException(exception))
		{
			return null;
		}
	}

	private static bool IsFileSystemException(Exception exception)
		=> exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException;
}
