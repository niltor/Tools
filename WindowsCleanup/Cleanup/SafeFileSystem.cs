using System.Text.RegularExpressions;

namespace WindowsCleanup.Cleanup;

internal readonly record struct SafeFileEntry(string Path, long Length);

internal static class SafeFileSystem
{
	internal static bool IsExistingSafeLocation(string path)
		=> !IsProtectedPath(path) && !HasReparsePointAncestor(path) && (Directory.Exists(path) || File.Exists(path));

	private static readonly Lazy<IReadOnlyList<string>> ProtectedRoots = new(BuildProtectedRoots);

	public static IEnumerable<SafeFileEntry> EnumerateFiles(
		CleanupLocation location,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (location.Kind == CleanupLocationKind.RecycleBin)
		{
			yield break;
		}

		var root = GetFullPath(location.Path);
		if (root is null || IsProtectedPath(root) || HasReparsePointAncestor(root))
		{
			yield break;
		}

		Regex? pattern = null;
		if (!string.IsNullOrWhiteSpace(location.Pattern))
		{
			var expression = "^" + Regex.Escape(location.Pattern!)
				.Replace("\\*", ".*", StringComparison.Ordinal)
				.Replace("\\?", ".", StringComparison.Ordinal) + "$";
			pattern = new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
		}

		if (File.Exists(root))
		{
			if (IsReparsePoint(root) ||
				(pattern is not null && !pattern.IsMatch(Path.GetFileName(root))) ||
				!IsOldEnough(root, location.MinimumAgeDays))
			{
				yield break;
			}

			if (TryGetLength(root, out var length))
			{
				yield return new SafeFileEntry(root, length);
			}

			yield break;
		}

		if (!Directory.Exists(root) || IsReparsePoint(root))
		{
			yield break;
		}

		var pending = new Stack<string>();
		pending.Push(root);
		var cutoff = location.MinimumAgeDays is > 0 ? DateTime.UtcNow.AddDays(-location.MinimumAgeDays.Value) : (DateTime?)null;

		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var current = pending.Pop();
			if (IsReparsePoint(current)) continue;
			FileSystemInfo[] entries;
			try
			{
				entries = new DirectoryInfo(current).GetFileSystemInfos();
			}
			catch (Exception exception) when (IsFileSystemException(exception))
			{
				continue;
			}

			foreach (var entry in entries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				SafeFileEntry? file = null;
			try
			{
					if (IsProtectedPath(entry.FullName) || (entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
					if (entry is DirectoryInfo)
					{
						if (location.Recursive) pending.Push(entry.FullName);
					}
					else if (entry is FileInfo info && (pattern is null || pattern.IsMatch(info.Name)) &&
						(cutoff is null || info.LastWriteTimeUtc <= cutoff.Value))
					{
						file = new SafeFileEntry(info.FullName, info.Length);
					}
				}
				catch (Exception exception) when (IsFileSystemException(exception))
				{
					continue;
				}

				if (file is { } result) yield return result;
			}
		}
	}

	public static CleanupExecutionResult DeleteLocation(CleanupLocation location)
	{
		if (IsProtectedPath(location.Path))
		{
			return CleanupExecutionResult.Empty;
		}

		// NuGet treats .nupkg.metadata as proof that a package was fully extracted.
		// Remove these markers first; if one is locked, preserve the whole package.
		var entries = location.NuGetPackageCache
			? EnumerateFiles(location).OrderBy(entry => Path.GetFileName(entry.Path) == ".nupkg.metadata" ? 0 : 1)
			: EnumerateFiles(location);
		var deletedFiles = 0;
		long deletedBytes = 0;
		var failedFiles = 0;
		var errors = new List<string>();

		foreach (var entry in entries)
		{
			try
			{
				if (HasReparsePointAncestor(entry.Path) || !File.Exists(entry.Path))
				{
					continue;
				}
				if (location.NuGetPackageCache && Path.GetFileName(entry.Path) != ".nupkg.metadata" && HasNuGetMarker(entry.Path, location.Path))
				{
					failedFiles++;
					continue;
				}

				File.Delete(entry.Path);
				deletedFiles++;
				deletedBytes += entry.Length;
			}
			catch (Exception exception) when (IsFileSystemException(exception))
			{
				failedFiles++;
				if (errors.Count < 5)
				{
					errors.Add($"{entry.Path}: {exception.Message}");
				}
			}
		}

		if (location.Kind == CleanupLocationKind.DirectoryContents && location.Recursive)
		{
			DeleteEmptyDirectories(location.Path, errors, ref failedFiles);
		}

		return new CleanupExecutionResult(deletedFiles, deletedBytes, failedFiles, errors);
	}

	private static bool HasNuGetMarker(string path, string root)
	{
		for (var parent = Path.GetDirectoryName(path); parent is not null && UserFolderDiscovery.IsSameOrDescendant(parent, root); parent = Path.GetDirectoryName(parent))
		{
			if (File.Exists(Path.Combine(parent, ".nupkg.metadata"))) return true;
		}
		return false;
	}

	public static bool IsReparsePoint(string path)
	{
		try
		{
			return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
		}
		catch (Exception exception) when (IsFileSystemException(exception))
		{
			return true;
		}
	}

	internal static bool HasReparsePointAncestor(string path)
	{
		try
		{
			for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
			{
				if ((Directory.Exists(current) || File.Exists(current)) && IsReparsePoint(current)) return true;
			}
			return false;
		}
		catch (Exception exception) when (IsFileSystemException(exception)) { return true; }
	}

	public static bool TryGetLength(string path, out long length)
	{
		try
		{
			length = new FileInfo(path).Length;
			return true;
		}
		catch (Exception exception) when (IsFileSystemException(exception))
		{
			length = 0;
			return false;
		}
	}

	private static bool IsOldEnough(string path, int? minimumAgeDays)
	{
		if (minimumAgeDays is null || minimumAgeDays <= 0)
		{
			return true;
		}

		try
		{
			var cutoff = DateTime.UtcNow.AddDays(-minimumAgeDays.Value);
			return File.GetLastWriteTimeUtc(path) <= cutoff;
		}
		catch (Exception exception) when (IsFileSystemException(exception))
		{
			// If the timestamp cannot be read, skip the file rather than making
			// an age-based cleanup decision without evidence.
			return false;
		}
	}

	private static void DeleteEmptyDirectories(string rootPath, List<string> errors, ref int failedFiles)
	{
		var root = GetFullPath(rootPath);
		if (root is null || IsProtectedPath(root) || !Directory.Exists(root) || HasReparsePointAncestor(root))
		{
			return;
		}

		var directories = new List<string>();
		var pending = new Stack<string>();
		pending.Push(root);

		while (pending.Count > 0)
		{
			var current = pending.Pop();
			if (HasReparsePointAncestor(current)) continue;
			directories.Add(current);
			IEnumerable<string> entries;
			try
			{
				entries = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly).ToArray();
			}
			catch (Exception exception) when (IsFileSystemException(exception))
			{
				continue;
			}

			foreach (var directory in entries)
			{
				if (!IsProtectedPath(directory) && !IsReparsePoint(directory))
				{
					pending.Push(directory);
				}
			}
		}

		foreach (var directory in directories.OrderByDescending(GetDepth))
		{
			if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			try
			{
				if (!HasReparsePointAncestor(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
				{
					Directory.Delete(directory, false);
				}
			}
			catch (Exception exception) when (IsFileSystemException(exception))
			{
				failedFiles++;
				if (errors.Count < 5)
				{
					errors.Add($"{directory}: {exception.Message}");
				}
			}
		}
	}

	private static int GetDepth(string path)
		=> path.Count(character => character is '\\' or '/');

	private static string? GetFullPath(string path)
	{
		try
		{
			return Path.GetFullPath(path);
		}
		catch (Exception exception) when (IsFileSystemException(exception))
		{
			return null;
		}
	}

	private static bool IsProtectedPath(string path)
	{
		var fullPath = GetFullPath(path);
		if (fullPath is null)
		{
			return true;
		}

		foreach (var protectedRoot in ProtectedRoots.Value)
		{
			if (string.Equals(fullPath, protectedRoot, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			var descendantPrefix = protectedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			if (fullPath.StartsWith(descendantPrefix, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	private static IReadOnlyList<string> BuildProtectedRoots()
	{
		var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
		var roots = new List<string?>
		{
			UserFolderDiscovery.GetDownloadsPath(),
			Combine(windows, "Installer"),
			Combine(windows, "WinSxS"),
			Combine(windows, "servicing"),
			Combine(windows, "System32", "config"),
			Combine(windows, "System32", "drivers"),
			Combine(windows, "System32", "winevt", "Logs"),
			Combine(windows, "System32", "DriverStore"),
			Combine(programData, "Package Cache"),
			Combine(programData, "Microsoft", "VisualStudio", "Packages")
		};

		try
		{
			var systemDrive = string.IsNullOrWhiteSpace(windows) ? null : Path.GetPathRoot(Path.GetFullPath(windows));
			if (!string.IsNullOrWhiteSpace(systemDrive))
			{
				roots.Add(Path.Combine(systemDrive, "Windows.old"));
			}
		}
		catch (Exception exception) when (IsFileSystemException(exception))
		{
			// An unavailable system root simply contributes no extra protected path.
		}

		return roots
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(path => GetFullPath(path!))
			.Where(path => path is not null)
			.Select(path => path!)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static string? Combine(string? root, params string[] parts)
	{
		if (string.IsNullOrWhiteSpace(root))
		{
			return null;
		}

		try
		{
			return Path.Combine(new[] { root }.Concat(parts).ToArray());
		}
		catch (Exception exception) when (IsFileSystemException(exception))
		{
			return null;
		}
	}

	private static bool IsFileSystemException(Exception exception)
		=> exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException;
}
