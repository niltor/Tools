using System.Text.RegularExpressions;

namespace WindowsCleanup.Cleanup;

internal readonly record struct SafeFileEntry(string Path, long Length);

internal static class SafeFileSystem
{
	public static IEnumerable<SafeFileEntry> EnumerateFiles(CleanupLocation location)
	{
		if (location.Kind == CleanupLocationKind.RecycleBin)
		{
			yield break;
		}

		var root = GetFullPath(location.Path);
		if (root is null)
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

		while (pending.Count > 0)
		{
			var current = pending.Pop();
			IEnumerable<string> entries;
			try
			{
				entries = Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly).ToArray();
			}
			catch (Exception exception) when (IsFileSystemException(exception))
			{
				continue;
			}

			foreach (var entry in entries)
			{
				if (IsReparsePoint(entry))
				{
					continue;
				}

				bool isDirectory;
			try
			{
					isDirectory = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
				}
				catch (Exception exception) when (IsFileSystemException(exception))
				{
					continue;
				}

				if (isDirectory)
				{
					if (location.Recursive)
					{
						pending.Push(entry);
					}

					continue;
				}

				if (pattern is not null && !pattern.IsMatch(Path.GetFileName(entry)))
				{
					continue;
				}

				if (!IsOldEnough(entry, location.MinimumAgeDays))
				{
					continue;
				}

				if (TryGetLength(entry, out var length))
				{
					yield return new SafeFileEntry(entry, length);
				}
			}
		}
	}

	public static CleanupExecutionResult DeleteLocation(CleanupLocation location)
	{
		var entries = EnumerateFiles(location).ToArray();
		var deletedFiles = 0;
		long deletedBytes = 0;
		var failedFiles = 0;
		var errors = new List<string>();

		foreach (var entry in entries)
		{
			try
			{
				if (IsReparsePoint(entry.Path) || !File.Exists(entry.Path))
				{
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
		if (root is null || !Directory.Exists(root) || IsReparsePoint(root))
		{
			return;
		}

		var directories = new List<string>();
		var pending = new Stack<string>();
		pending.Push(root);

		while (pending.Count > 0)
		{
			var current = pending.Pop();
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
				if (!IsReparsePoint(directory))
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
				if (!Directory.EnumerateFileSystemEntries(directory).Any())
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

	private static bool IsFileSystemException(Exception exception)
		=> exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException;
}
