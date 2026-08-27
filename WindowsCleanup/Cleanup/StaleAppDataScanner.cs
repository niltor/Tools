using Spectre.Console;
using WindowsCleanup.Registry;

namespace WindowsCleanup.Cleanup;

public sealed record AppDataCandidate(string Path, DateTime LastWriteTimeUtc, long Bytes)
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

public sealed record AppDataDeleteResult(
	int DeletedFiles,
	long DeletedBytes,
	int FailedFiles,
	int SkippedCandidates,
	IReadOnlyList<string> Errors);

public static class StaleAppDataScanner
{
	private static readonly HashSet<string> ProtectedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"Microsoft", "Packages", "Temp", "CrashDumps", "ConnectedDevicesPlatform", "Comms",
		"D3DSCache", "NVIDIA", "NVIDIA Corporation", "AMD", "Intel", "Programs", "WindowsApps", "OneDrive",
		"Cache", "npm", "npm-cache", "NuGet", "pip", "Yarn", "pnpm", "go-build", "Gradle", "JetBrains", "Android",
		"Docker", ".cache", ".ollama", "huggingface", "HuggingFace",
		".codex", ".gemini", "Claude", "OpenAI", "ChatGPT", "Cursor", "Windsurf",
		"Tencent", "WeChat", "QQ", "Discord", "Slack", "Signal", "Skype for Desktop", "Zoom",
		"DingTalk", "钉钉", "Feishu", "飞书", "Lark",
		"Douyin", "抖音", "ByteDance", "bilibili", "Bilibili", "Douyu", "斗鱼", "Huya", "虎牙",
		"Kwai", "快手", "TikTok", "EpicGamesLauncher", "Battle.net", "GOG.com", "Steam",
		"Riot Games", "Ubisoft Game Launcher", "Rockstar Games"
	};

	public static IReadOnlyList<AppDataCandidate> Find(int maximumCandidates = 30)
	{
		var candidates = new List<AppDataCandidate>();
		var cutoff = DateTime.UtcNow.AddDays(-90);
		var installedApplications = RegistryCleaner.GetInstalledApplications();
		var installedNames = installedApplications
			.Select(application => RegistryCleaner.NormalizeName(application.DisplayName))
			.Where(name => name.Length > 0)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var roots = GetAppDataRoots();

		foreach (var root in roots.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (!Directory.Exists(root) || SafeFileSystem.IsReparsePoint(root))
			{
				continue;
			}

			IEnumerable<string> directories;
			try
			{
				directories = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
			{
				continue;
			}

			foreach (var directory in directories)
			{
				if (candidates.Count >= maximumCandidates || SafeFileSystem.IsReparsePoint(directory))
				{
					break;
				}

				var name = Path.GetFileName(directory);
				if (ProtectedDirectoryNames.Contains(name))
				{
					continue;
				}

				DateTime lastWrite;
				try
				{
					lastWrite = Directory.GetLastWriteTimeUtc(directory);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					continue;
				}

				if (lastWrite >= cutoff ||
					LooksInstalled(name, installedNames) ||
					IsRegisteredApplicationDirectory(directory, installedApplications) ||
					ContainsExecutable(directory))
				{
					continue;
				}

				var bytes = 0L;
				foreach (var entry in SafeFileSystem.EnumerateFiles(new CleanupLocation(directory)))
				{
					bytes += entry.Length;
				}

				candidates.Add(new AppDataCandidate(directory, lastWrite, bytes));
			}
		}

		return candidates.OrderByDescending(candidate => candidate.Bytes).ToArray();
	}

	public static AppDataDeleteResult Delete(IEnumerable<AppDataCandidate> candidates)
	{
		var deletedFiles = 0;
		long deletedBytes = 0;
		var failedFiles = 0;
		var skippedCandidates = 0;
		var errors = new List<string>();
		var installedApplications = RegistryCleaner.GetInstalledApplications();
		var installedNames = installedApplications
			.Select(application => RegistryCleaner.NormalizeName(application.DisplayName))
			.Where(value => value.Length > 0)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var roots = GetAppDataRoots();
		var cutoff = DateTime.UtcNow.AddDays(-90);

		foreach (var candidate in candidates)
		{
			try
			{
				if (!IsApprovedCandidate(candidate.Path, roots, installedApplications, installedNames, cutoff))
				{
					skippedCandidates++;
					continue;
				}

				var result = SafeFileSystem.DeleteLocation(new CleanupLocation(
					candidate.Path,
					MinimumAgeDays: 90));
				deletedFiles += result.DeletedFiles;
				deletedBytes += result.DeletedBytes;
				failedFiles += result.FailedFiles;
				AddErrors(errors, result.Errors);

				if (Directory.Exists(candidate.Path) && !SafeFileSystem.IsReparsePoint(candidate.Path))
				{
					try
					{
						if (!Directory.EnumerateFileSystemEntries(candidate.Path).Any())
						{
							Directory.Delete(candidate.Path, recursive: false);
						}
					}
					catch (Exception exception) when (IsFileSystemException(exception))
					{
						failedFiles++;
						AddError(errors, candidate.Path, exception.Message);
					}
				}
			}
			catch (Exception exception) when (IsFileSystemException(exception))
			{
				failedFiles++;
				AddError(errors, candidate.Path, exception.Message);
			}
		}

		return new AppDataDeleteResult(deletedFiles, deletedBytes, failedFiles, skippedCandidates, errors);
	}

	private static IReadOnlyList<string> GetAppDataRoots()
	{
		var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		var localLow = string.IsNullOrWhiteSpace(local) ? string.Empty : Path.Combine(local, "..", "LocalLow");
		return new[] { local, roaming, localLow }
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(path => Path.GetFullPath(path!))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static bool IsApprovedCandidate(
		string path,
		IReadOnlyList<string> roots,
		IReadOnlyList<InstalledApplication> installedApplications,
		IReadOnlySet<string> installedNames,
		DateTime cutoff)
	{
		if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) || SafeFileSystem.IsReparsePoint(path))
		{
			return false;
		}

		var fullPath = Path.GetFullPath(path);
		var parent = Directory.GetParent(fullPath)?.FullName;
		if (parent is null || !roots.Any(root => string.Equals(
			Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			StringComparison.OrdinalIgnoreCase)))
		{
			return false;
		}

		var name = Path.GetFileName(fullPath);
		if (ProtectedDirectoryNames.Contains(name) || ContainsExecutable(fullPath) ||
			LooksInstalled(name, installedNames) ||
			IsRegisteredApplicationDirectory(fullPath, installedApplications))
		{
			return false;
		}

		return Directory.GetLastWriteTimeUtc(fullPath) < cutoff;
	}

	private static void AddErrors(ICollection<string> errors, IEnumerable<string> newErrors)
	{
		foreach (var error in newErrors)
		{
			if (errors.Count >= 5)
			{
				break;
			}

			errors.Add(error);
		}
	}

	private static void AddError(ICollection<string> errors, string path, string message)
	{
		if (errors.Count < 5)
		{
			errors.Add($"{path}: {message}");
		}
	}

	private static bool LooksInstalled(string directoryName, IReadOnlySet<string> installedNames)
	{
		var normalizedDirectory = RegistryCleaner.NormalizeName(directoryName);
		if (normalizedDirectory.Length < 4)
		{
			return false;
		}

		return installedNames.Any(installed =>
			installed.Contains(normalizedDirectory, StringComparison.OrdinalIgnoreCase) ||
			normalizedDirectory.Contains(installed, StringComparison.OrdinalIgnoreCase));
	}

	private static bool IsRegisteredApplicationDirectory(
		string directory,
		IReadOnlyList<InstalledApplication> installedApplications)
	{
		foreach (var application in installedApplications)
		{
			foreach (var registeredPath in new[] { application.InstallLocation, application.ExecutablePath }
				.Where(path => !string.IsNullOrWhiteSpace(path)))
			{
				if (UserFolderDiscovery.IsSameOrDescendant(directory, registeredPath) ||
					UserFolderDiscovery.IsSameOrDescendant(registeredPath, directory))
				{
					return true;
				}
			}
		}

		return false;
	}

	private static bool ContainsExecutable(string directory)
	{
		try
		{
			return SafeFileSystem.EnumerateFiles(new CleanupLocation(directory, Pattern: "*.exe")).Any();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return true;
		}
	}

	private static bool IsFileSystemException(Exception exception)
		=> exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException;
}
