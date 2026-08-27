using WindowsCleanup.Localization;
using Spectre.Console;
using WindowsCleanup.Registry;

namespace WindowsCleanup.Cleanup;

public sealed record InstallerCandidate(
	string Path,
	long Bytes,
	DateTime LastWriteTimeUtc,
	LocalizedText Source,
	string RootPath)
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

public static class InstallerCandidateScanner
{
	private const long MinimumBytes = 20L * 1024 * 1024;

	private static readonly string[] InstallerExtensions =
	{
		".appx",
		".appxbundle",
		".cab",
		".crx",
		".crx3",
		".msp",
		".msi",
		".msix",
		".msixbundle",
		".msu",
		".nupkg",
		".pkg",
		".appinstaller",
		".msixupload",
		".vsix",
		".vsixbundle"
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

				if (!SafeFileSystem.TryGetLength(candidate.Path, out var length) || length < MinimumBytes)
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
		var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
		var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

		// User-owned document roots are intentionally excluded. They are not
		// application-maintained update caches and remain outside this review scan.
		AddRoot(roots, Combine(local, "Google", "Update", "Download"), new("Google 更新暂存目录", "Google updater staging"));
		AddRoot(roots, Combine(programData, "Google", "Update", "Download"), new("Google 更新暂存目录", "Google updater staging"));
		AddRoot(roots, Combine(programFiles, "Google", "Update", "Download"), new("Google 更新暂存目录", "Google updater staging"));
		AddRoot(roots, Combine(programFilesX86, "Google", "Update", "Download"), new("Google 更新暂存目录", "Google updater staging"));
		AddRoot(roots, Combine(local, "Microsoft", "EdgeUpdate", "Download"), new("Edge 更新暂存目录", "Edge updater staging"));
		AddRoot(roots, Combine(programData, "Microsoft", "EdgeUpdate", "Download"), new("Edge 更新暂存目录", "Edge updater staging"));
		AddRoot(roots, Combine(programFiles, "Microsoft", "EdgeUpdate", "Download"), new("Edge 更新暂存目录", "Edge updater staging"));
		AddRoot(roots, Combine(programFilesX86, "Microsoft", "EdgeUpdate", "Download"), new("Edge 更新暂存目录", "Edge updater staging"));

		AddRoot(roots, Combine(local, "SquirrelTemp"), new("Squirrel 应用更新暂存目录", "Squirrel application update staging"));
		AddRoot(roots, Combine(local, "Microsoft", "Visual Studio Code", "Update"), new("VS Code 更新暂存目录", "VS Code update staging"));
		AddRoot(roots, Combine(local, "Microsoft", "Visual Studio Code Insiders", "Update"), new("VS Code Insiders 更新暂存目录", "VS Code Insiders update staging"));
		AddRoot(roots, Combine(local, "Programs", "Microsoft VS Code", "Update"), new("VS Code 更新暂存目录", "VS Code update staging"));
		AddRoot(roots, Combine(local, "Programs", "Microsoft VS Code Insiders", "Update"), new("VS Code Insiders 更新暂存目录", "VS Code Insiders update staging"));
		AddRoot(roots, Combine(roaming, "Microsoft", "Teams", "packages", "squirreltemp"), new("Teams 更新暂存目录", "Teams update staging"));
		AddRoot(roots, Combine(local, "Microsoft", "Teams", "packages", "squirreltemp"), new("Teams 更新暂存目录", "Teams update staging"));
		AddRoot(roots, Combine(local, "Adobe", "AAMUpdater"), new("Adobe 更新暂存目录", "Adobe update staging"));
		AddRoot(roots, Combine(local, "Adobe", "ARM"), new("Adobe ARM 更新暂存目录", "Adobe ARM update staging"));
		AddRoot(roots, Combine(programData, "Adobe", "ARM"), new("Adobe ARM 更新暂存目录", "Adobe ARM update staging"));

		var systemRoot = GetSystemDriveRoot(windows);
		if (!string.IsNullOrWhiteSpace(systemRoot))
		{
			AddRoot(roots, Combine(systemRoot, "AMD"), new("AMD 安装包目录", "AMD installer staging"));
			AddRoot(roots, Combine(systemRoot, "Intel"), new("Intel 安装包目录", "Intel installer staging"));
			AddRoot(roots, Combine(systemRoot, "NVIDIA"), new("NVIDIA 安装包目录", "NVIDIA installer staging"));
		}

		AddRoot(roots, Combine(programData, "NVIDIA Corporation", "Downloader"), new("NVIDIA 更新暂存目录", "NVIDIA updater staging"));
		AddRoot(roots, Combine(programData, "NVIDIA Corporation", "GeForce Experience", "Update"), new("NVIDIA GeForce 更新目录", "NVIDIA GeForce update staging"));
		AddRoot(roots, Combine(programData, "NVIDIA Corporation", "NVIDIA App", "UpdateFramework", "ota-artifacts"), new("NVIDIA App 更新包目录", "NVIDIA App update payloads"));
		AddRoot(roots, Combine(programData, "NVIDIA Corporation", "NvApp-UpdateFramework", "ota-artifacts"), new("NVIDIA App 更新包目录", "NVIDIA App update payloads"));
		AddRoot(roots, Combine(programData, "NVIDIA", "NvBackend", "Updatus", "DownloadManager"), new("NVIDIA 更新暂存目录", "NVIDIA updater staging"));
		AddRoot(roots, Combine(programData, "NVIDIA", "Updatus", "DownloadManager"), new("NVIDIA 更新暂存目录", "NVIDIA updater staging"));
		AddRoot(roots, Combine(programFiles, "NVIDIA Corporation", "Installer2"), new("NVIDIA 驱动安装缓存（需确认）", "NVIDIA driver installer cache (review)"));
		AddRoot(roots, Combine(programFilesX86, "NVIDIA Corporation", "Installer2"), new("NVIDIA 驱动安装缓存（需确认）", "NVIDIA driver installer cache (review)"));

		foreach (var application in RegistryCleaner.GetInstalledApplications())
		{
			AddApplicationUpdaterRoots(roots, application);
		}

		return roots;
	}

	private static void AddApplicationUpdaterRoots(ICollection<CandidateRoot> roots, InstalledApplication application)
	{
		if (!IsKnownUpdaterApplication(application) ||
			string.IsNullOrWhiteSpace(application.InstallLocation) ||
			!Directory.Exists(application.InstallLocation))
		{
			return;
		}

		var label = new LocalizedText(
			$"{application.DisplayName} 更新暂存目录（需确认）",
			$"{application.DisplayName} update staging (review)");
		foreach (var ancestor in EnumerateAncestors(application.InstallLocation, maximumDepth: 2))
		{
			foreach (var directoryName in new[] { "Update", "updates", "Download", "Patches", "patches" })
			{
				AddRoot(roots, Combine(ancestor, directoryName), label);
			}

			// Squirrel applications keep downloaded full/delta packages in a
			// sibling packages directory. It is review-only because it can affect
			// offline updates or rollback, even though the app itself remains usable.
			if (File.Exists(Combine(ancestor, "Update.exe")) || File.Exists(Combine(ancestor, "update.exe")))
			{
				AddRoot(roots, Combine(ancestor, "packages"), label);
				AddRoot(roots, Combine(ancestor, "packages", "temp"), label);
			}
		}
	}

	private static bool IsKnownUpdaterApplication(InstalledApplication application)
	{
		var identity = RegistryCleaner.NormalizeName($"{application.DisplayName} {application.Publisher}");
		return new[]
		{
			"GOOGLECHROME", "GOOGLEUPDATE", "MICROSOFTEDGE", "VISUALSTUDIOCODE", "VSCODE",
			"DISCORD", "SLACK", "MICROSOFTTEAMS", "ZOOM", "SPOTIFY", "ADOBE", "NVIDIA",
			"GEFORCE", "AMD", "INTEL", "EPICGAMES", "BATTLENET", "BLIZZARD", "UBISOFT",
			"RIOT", "ROCKSTAR", "STEAM", "GOG"
		}.Any(marker => identity.Contains(marker, StringComparison.OrdinalIgnoreCase));
	}

	private static IEnumerable<string> EnumerateAncestors(string path, int maximumDepth)
	{
		string? current;
		try
		{
			current = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			yield break;
		}

		for (var depth = 0; depth < maximumDepth && !string.IsNullOrWhiteSpace(current); depth++)
		{
			yield return current;
			string? parent;
			try
			{
				parent = Directory.GetParent(current)?.FullName;
			}
			catch (Exception exception) when (IsFileSystemException(exception))
			{
				yield break;
			}
			if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
			{
				yield break;
			}

			current = parent;
		}
	}

	private static void AddRoot(ICollection<CandidateRoot> roots, string? path, LocalizedText label)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return;
		}

		try
		{
			var fullPath = Path.GetFullPath(path!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (UserFolderDiscovery.IsSameOrDescendant(fullPath, UserFolderDiscovery.GetDownloadsPath()))
			{
				return;
			}

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

	private static string? Combine(string? root, params string?[] parts)
	{
		if (string.IsNullOrWhiteSpace(root) || parts.Any(string.IsNullOrWhiteSpace))
		{
			return null;
		}

		try
		{
			return Path.Combine(new[] { root! }.Concat(parts.Select(part => part!)).ToArray());
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return null;
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
			(rootPath is not null && IsVendorStagingRoot(rootPath) &&
				(extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
				 extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
				 extension.Equals(".7z", StringComparison.OrdinalIgnoreCase) ||
				 extension.Equals(".rar", StringComparison.OrdinalIgnoreCase) ||
				 extension.Equals(".iso", StringComparison.OrdinalIgnoreCase)));
	}

	private static bool IsVendorStagingRoot(string rootPath)
	{
		var name = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		return name.Equals("AMD", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("Intel", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("Installer2", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("AAMUpdater", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("ARM", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("SquirrelTemp", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("Update", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("updates", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("Download", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("Downloads", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("Patches", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("patches", StringComparison.OrdinalIgnoreCase) ||
			rootPath.Contains(Path.Combine("NVIDIA Corporation", "Downloader"), StringComparison.OrdinalIgnoreCase) ||
			rootPath.Contains(Path.Combine("NVIDIA Corporation", "Installer2"), StringComparison.OrdinalIgnoreCase) ||
			rootPath.Contains(Path.Combine("NVIDIA Corporation", "NVIDIA App", "UpdateFramework", "ota-artifacts"), StringComparison.OrdinalIgnoreCase) ||
			rootPath.Contains(Path.Combine("NVIDIA Corporation", "NvApp-UpdateFramework", "ota-artifacts"), StringComparison.OrdinalIgnoreCase) ||
			rootPath.Contains(Path.Combine("Google", "Update", "Download"), StringComparison.OrdinalIgnoreCase) ||
			rootPath.Contains(Path.Combine("EdgeUpdate", "Download"), StringComparison.OrdinalIgnoreCase) ||
			rootPath.Contains(Path.Combine("Microsoft", "Teams", "packages", "squirreltemp"), StringComparison.OrdinalIgnoreCase);
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
