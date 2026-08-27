using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace WindowsCleanup.Cleanup;

internal static class SteamLibraryDiscovery
{
	private static readonly Regex LibraryPathPattern = new(
		@"""path""\s+""(?<path>[^""]+)""",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
		TimeSpan.FromSeconds(1));

	public static IReadOnlyList<string> FindLibraries()
	{
		if (!OperatingSystem.IsWindows())
		{
			return Array.Empty<string>();
		}

		var roots = new List<string>();
		foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
		{
			foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
			{
				try
				{
					using var baseKey = RegistryKey.OpenBaseKey(hive, view);
					using var key = baseKey.OpenSubKey(@"Software\Valve\Steam", writable: false);
					AddPath(roots, key?.GetValue("SteamPath")?.ToString());
					AddPath(roots, key?.GetValue("InstallPath")?.ToString());
				}
				catch (Exception exception) when (IsRegistryException(exception))
				{
					// Steam may not be registered in one of the registry views.
				}
			}
		}

		var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
		AddPath(roots, Combine(programFilesX86, "Steam"));
		AddPath(roots, Combine(programFiles, "Steam"));

		var libraries = new List<string>();
		foreach (var root in roots)
		{
			AddDistinct(libraries, root);
			var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
			try
			{
				if (!File.Exists(libraryFile))
				{
					continue;
				}

				foreach (Match match in LibraryPathPattern.Matches(File.ReadAllText(libraryFile)))
				{
					AddDistinct(libraries, match.Groups["path"].Value.Replace("\\\\", "\\", StringComparison.Ordinal));
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
			{
				// A missing or locked library manifest must not stop cleanup discovery.
			}
		}

		return libraries.ToArray();
	}

	private static void AddPath(ICollection<string> paths, string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return;
		}

		try
		{
			AddDistinct(paths, Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			// Ignore malformed optional paths.
		}
	}

	private static void AddDistinct(ICollection<string> paths, string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return;
		}

		try
		{
			var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (fullPath.Length > 0 && paths.All(existing => !string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase)))
			{
				paths.Add(fullPath);
			}
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			// Ignore malformed optional paths.
		}
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
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return null;
		}
	}

	private static bool IsRegistryException(Exception exception)
		=> exception is UnauthorizedAccessException or IOException or System.Security.SecurityException or ObjectDisposedException or ArgumentException;
}
