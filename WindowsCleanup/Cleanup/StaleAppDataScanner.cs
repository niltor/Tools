using WindowsCleanup.Registry;

namespace WindowsCleanup.Cleanup;

public sealed record AppDataCandidate(string Path, DateTime LastWriteTimeUtc, long Bytes);

public static class StaleAppDataScanner
{
	private static readonly HashSet<string> ProtectedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"Microsoft", "Packages", "Temp", "CrashDumps", "ConnectedDevicesPlatform", "Comms",
		"D3DSCache", "NVIDIA", "AMD", "Intel", "Programs", "WindowsApps", "OneDrive"
	};

	public static IReadOnlyList<AppDataCandidate> Find(int maximumCandidates = 30)
	{
		var candidates = new List<AppDataCandidate>();
		var cutoff = DateTime.UtcNow.AddDays(-90);
		var installedNames = RegistryCleaner.GetInstalledApplicationNames();
		var roots = new[]
		{
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } local
				? Path.Combine(local, "..", "LocalLow")
				: string.Empty
		};

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

				if (lastWrite >= cutoff || LooksInstalled(name, installedNames) || ContainsExecutable(directory))
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

	private static bool ContainsExecutable(string directory)
	{
		try
		{
			return Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly).Any();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return true;
		}
	}
}
