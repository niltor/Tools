using WindowsCleanup.Localization;
using Spectre.Console;

namespace WindowsCleanup.Cleanup;

public sealed record InstallerCandidate(
	string Path,
	long Bytes,
	DateTime LastWriteTimeUtc,
	LocalizedText Source,
	string RootPath)
{
	public override string ToString() => Markup.Escape(Path);
}

public static class InstallerCandidateScanner
{
	private const int MinimumAgeDays = 30;
	private const long MinimumBytes = 20L * 1024 * 1024;

	private static readonly string[] InstallerExtensions =
	{
		".appx",
		".appxbundle",
		".cab",
		".msp",
		".msi",
		".msix",
		".msixbundle",
		".msu"
	};

	private static readonly string[] NamedPackageExtensions =
	{
		".7z",
		".exe",
		".iso",
		".rar",
		".zip"
	};

	private static readonly string[] InstallerNameMarkers =
	{
		"driver",
		"hotfix",
		"install",
		"installer",
		"offline",
		"patch",
		"redistributable",
		"setup",
		"update",
		"upgrade"
	};

	public static IReadOnlyList<InstallerCandidate> Find()
	{
		if (!OperatingSystem.IsWindows())
		{
			return Array.Empty<InstallerCandidate>();
		}

		var candidates = new List<InstallerCandidate>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var cutoff = DateTime.UtcNow.AddDays(-MinimumAgeDays);

		foreach (var root in GetRoots())
		{
			var location = new CleanupLocation(root.Path);
			foreach (var entry in SafeFileSystem.EnumerateFiles(location))
			{
				if (!seen.Add(entry.Path) || entry.Length < MinimumBytes || !IsPackageName(entry.Path, root.Path))
				{
					continue;
				}

				DateTime lastWriteTimeUtc;
				try
				{
					lastWriteTimeUtc = File.GetLastWriteTimeUtc(entry.Path);
				}
				catch (Exception exception) when (IsFileSystemException(exception))
				{
					continue;
				}

				if (lastWriteTimeUtc > cutoff)
				{
					continue;
				}

				candidates.Add(new InstallerCandidate(entry.Path, entry.Length, lastWriteTimeUtc, root.Label, root.Path));
			}
		}

		return candidates
			.OrderByDescending(candidate => candidate.Bytes)
			.ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
			.Take(200)
			.ToArray();
	}

	public static CleanupExecutionResult Delete(IEnumerable<InstallerCandidate> candidates)
	{
		var deletedFiles = 0;
		long deletedBytes = 0;
		var failedFiles = 0;
		var errors = new List<string>();
		var roots = GetRoots().ToArray();

		foreach (var candidate in candidates)
		{
			try
			{
				var root = roots.FirstOrDefault(item =>
					string.Equals(item.Path, candidate.RootPath, StringComparison.OrdinalIgnoreCase));
				if (root is null || !IsWithinRoot(candidate.Path, root.Path) || !IsPackageName(candidate.Path, root.Path))
				{
					throw new IOException("The candidate is outside the approved installer locations.");
				}

				if (!File.Exists(candidate.Path) || SafeFileSystem.IsReparsePoint(candidate.Path))
				{
					continue;
				}

				if (!SafeFileSystem.TryGetLength(candidate.Path, out var length))
				{
					continue;
				}

				File.Delete(candidate.Path);
				deletedFiles++;
				deletedBytes += length;
			}
			catch (Exception exception) when (IsFileSystemException(exception))
			{
				failedFiles++;
				if (errors.Count < 5)
				{
					errors.Add($"{candidate.Path}: {exception.Message}");
				}
			}
		}

		return new CleanupExecutionResult(deletedFiles, deletedBytes, failedFiles, errors);
	}

	private static IReadOnlyList<CandidateRoot> GetRoots()
	{
		var roots = new List<CandidateRoot>();
		var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
		var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

		AddRoot(roots,
			Path.Combine(userProfile, "Downloads"),
			new("用户 Downloads 目录", "Current-user Downloads"));

		var systemRoot = GetSystemDriveRoot(windows);
		if (!string.IsNullOrWhiteSpace(systemRoot))
		{
			AddRoot(roots, Path.Combine(systemRoot, "AMD"), new("AMD 安装包目录", "AMD installer staging"));
			AddRoot(roots, Path.Combine(systemRoot, "Intel"), new("Intel 安装包目录", "Intel installer staging"));
			AddRoot(roots, Path.Combine(systemRoot, "NVIDIA"), new("NVIDIA 安装包目录", "NVIDIA installer staging"));
		}

		AddRoot(roots,
			Path.Combine(programData, "NVIDIA Corporation", "Downloader"),
			new("NVIDIA 下载目录", "NVIDIA download staging"));
		AddRoot(roots,
			Path.Combine(programFiles, "NVIDIA Corporation", "Installer2"),
			new("NVIDIA 驱动安装缓存（需确认）", "NVIDIA driver installer cache (review)"));
		AddRoot(roots,
			Path.Combine(programFilesX86, "NVIDIA Corporation", "Installer2"),
			new("NVIDIA 驱动安装缓存（需确认）", "NVIDIA driver installer cache (review)"));

		return roots;
	}

	private static void AddRoot(ICollection<CandidateRoot> roots, string path, LocalizedText label)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return;
		}

		try
		{
			var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (fullPath.Length > 0 && roots.All(root => !string.Equals(root.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
			{
				roots.Add(new CandidateRoot(fullPath, label));
			}
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			// A missing or malformed optional root is simply omitted.
		}
	}

	private static bool IsPackageName(string path, string? rootPath = null)
	{
		var extension = System.IO.Path.GetExtension(path);
		if (InstallerExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
		{
			return true;
		}

		if (!NamedPackageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
		{
			return false;
		}

		return InstallerNameMarkers.Any(marker => Path.GetFileNameWithoutExtension(path).Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
			(extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && rootPath is not null && IsVendorStagingRoot(rootPath));
	}

	private static bool IsVendorStagingRoot(string rootPath)
	{
		var name = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		return name.Equals("AMD", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("Intel", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("Installer2", StringComparison.OrdinalIgnoreCase) ||
			rootPath.Contains(Path.Combine("NVIDIA Corporation", "Downloader"), StringComparison.OrdinalIgnoreCase) ||
			rootPath.Contains(Path.Combine("NVIDIA Corporation", "Installer2"), StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsWithinRoot(string path, string root)
	{
		try
		{
			var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return false;
		}
	}

	private static string? GetSystemDriveRoot(string windowsPath)
	{
		try
		{
			return string.IsNullOrWhiteSpace(windowsPath)
				? null
				: Path.GetPathRoot(Path.GetFullPath(windowsPath));
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return null;
		}
	}

	private static bool IsFileSystemException(Exception exception)
		=> exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException;

	private sealed record CandidateRoot(string Path, LocalizedText Label);
}
