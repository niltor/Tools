using Microsoft.Win32;

namespace WindowsCleanup.Cleanup;

internal static class UserFolderDiscovery
{
	private const string DownloadsKnownFolderId = "{374DE290-123F-4565-9164-39C4925E467B}";
	private const string DocumentsKnownFolderId = "{FDD39AD0-238F-46AF-ADB4-6C85480369C7}";
	private const string MusicKnownFolderId = "{4BD8D571-6D19-48D3-BE97-422220080E43}";
	private const string VideosKnownFolderId = "{18989B1D-99B5-455B-841C-AB7C74E4DDFC}";
	private static readonly Lazy<string?> DownloadsPath = new(() => DiscoverPath(
		DownloadsKnownFolderId,
		Environment.SpecialFolder.UserProfile,
		"Downloads"));
	private static readonly Lazy<string?> DocumentsPath = new(() => DiscoverPath(
		DocumentsKnownFolderId,
		Environment.SpecialFolder.MyDocuments,
		"Documents"));
	private static readonly Lazy<string?> MusicPath = new(() => DiscoverPath(
		MusicKnownFolderId,
		Environment.SpecialFolder.MyMusic,
		"Music"));
	private static readonly Lazy<string?> VideosPath = new(() => DiscoverPath(
		VideosKnownFolderId,
		Environment.SpecialFolder.MyVideos,
		"Videos"));

	public static string? GetDownloadsPath()
		=> DownloadsPath.Value;

	public static string? GetDocumentsPath()
		=> DocumentsPath.Value;

	public static string? GetMusicPath()
		=> MusicPath.Value;

	public static string? GetVideosPath()
		=> VideosPath.Value;

	public static string? GetPath(string knownFolderId, Environment.SpecialFolder fallbackFolder, string fallbackName)
		=> DiscoverPath(knownFolderId, fallbackFolder, fallbackName);

	private static string? DiscoverPath(
		string knownFolderId,
		Environment.SpecialFolder fallbackFolder,
		string fallbackName)
	{
		var fallbackRoot = Environment.GetFolderPath(fallbackFolder);
		var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var fallback = fallbackFolder == Environment.SpecialFolder.UserProfile
			? Combine(profile, fallbackName)
			: string.IsNullOrWhiteSpace(fallbackRoot) ? Combine(profile, fallbackName) : fallbackRoot;
		if (!OperatingSystem.IsWindows())
		{
			return fallback;
		}

		try
		{
			using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
				@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
				writable: false);
			var configuredPath = key?.GetValue(knownFolderId)?.ToString();
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
