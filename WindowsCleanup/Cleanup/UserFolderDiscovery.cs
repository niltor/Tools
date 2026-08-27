using Microsoft.Win32;

namespace WindowsCleanup.Cleanup;

internal static class UserFolderDiscovery
{
	private const string DownloadsKnownFolderId = "{374DE290-123F-4565-9164-39C4925E467B}";
	private static readonly Lazy<string?> DownloadsPath = new(DiscoverDownloadsPath);

	public static string? GetDownloadsPath()
		=> DownloadsPath.Value;

	private static string? DiscoverDownloadsPath()
	{
		var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var fallback = Combine(profile, "Downloads");
		if (!OperatingSystem.IsWindows())
		{
			return fallback;
		}

		try
		{
			using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
				@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
				writable: false);
			var configuredPath = key?.GetValue(DownloadsKnownFolderId)?.ToString();
			var expandedPath = Expand(configuredPath);
			return string.IsNullOrWhiteSpace(expandedPath) ? fallback : expandedPath;
		}
		catch (Exception exception) when (IsRegistryException(exception))
		{
			return fallback;
		}
	}

	public static bool IsSameOrDescendant(string? path, string? root)
	{
		if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
		{
			return false;
		}

		try
		{
			var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
				fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return false;
		}
	}

	private static string? Expand(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}

		try
		{
			var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
			return Path.IsPathRooted(expanded) ? Path.GetFullPath(expanded) : null;
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return null;
		}
	}

	private static string? Combine(string? root, string part)
	{
		if (string.IsNullOrWhiteSpace(root))
		{
			return null;
		}

		try
		{
			return Path.Combine(root, part);
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return null;
		}
	}

	private static bool IsRegistryException(Exception exception)
		=> exception is UnauthorizedAccessException or IOException or System.Security.SecurityException or ObjectDisposedException or ArgumentException;
}
