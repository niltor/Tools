#pragma warning disable CA1416

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WindowsCleanup.Migration;

internal static class KnownFolderPathService
{
	private const string UserShellFoldersPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
	private const string ShellFoldersPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders";

	public static bool TrySetPath(Guid knownFolderId, string destination, out string error)
	{
		error = string.Empty;
		if (!OperatingSystem.IsWindows())
		{
			error = "Known folders are only supported on Windows.";
			return false;
		}

		var previousUserShell = new Dictionary<string, RegistryValueSnapshot?>(StringComparer.OrdinalIgnoreCase);
		var previousShell = new Dictionary<string, RegistryValueSnapshot?>(StringComparer.OrdinalIgnoreCase);
		try
		{
			var valueNames = GetRegistryValueNames(knownFolderId);
			using var userShellFolders = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UserShellFoldersPath, writable: true);
			using var shellFolders = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ShellFoldersPath, writable: true);
			if (userShellFolders is null || shellFolders is null)
			{
				error = "Windows known-folder registry keys are not writable.";
				return false;
			}

			foreach (var valueName in valueNames)
			{
				previousUserShell[valueName] = ReadValue(userShellFolders, valueName);
				previousShell[valueName] = ReadValue(shellFolders, valueName);
			}

			foreach (var valueName in valueNames)
			{
				userShellFolders.SetValue(valueName, destination, RegistryValueKind.ExpandString);
				shellFolders.SetValue(valueName, destination, RegistryValueKind.String);
			}

			var id = knownFolderId;
			var result = SHSetKnownFolderPath(ref id, 0, IntPtr.Zero, destination);
			if (result != 0)
			{
				throw new Win32Exception(result, $"SHSetKnownFolderPath failed (0x{result:X8}).");
			}

			return true;
		}
		catch (Exception exception) when (IsConfigurationException(exception))
		{
			error = exception.Message;
			TryRestore(previousUserShell, UserShellFoldersPath);
			TryRestore(previousShell, ShellFoldersPath);

			return false;
		}
	}

	private static RegistryValueSnapshot? ReadValue(RegistryKey key, string valueName)
	{
		if (key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value)
		{
			return null;
		}

		return new RegistryValueSnapshot(value, key.GetValueKind(valueName));
	}

	private static void TryRestore(
		IReadOnlyDictionary<string, RegistryValueSnapshot?> snapshots,
		string keyPath)
	{
		try
		{
			using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
			if (key is null)
			{
				return;
			}

			foreach (var (valueName, snapshot) in snapshots)
			{
				if (snapshot is null)
				{
					key.DeleteValue(valueName, throwOnMissingValue: false);
				}
				else
				{
					key.SetValue(valueName, snapshot.Value, snapshot.Kind);
				}
			}
		}
		catch (Exception exception) when (IsConfigurationException(exception))
		{
			// Keep the original configuration error. The copied data remains in the
			// destination so the user can recover it if rollback is not possible.
		}
	}

	[DllImport("shell32.dll", ExactSpelling = true)]
	private static extern int SHSetKnownFolderPath(
		ref Guid rfid,
		uint dwFlags,
		IntPtr hToken,
		[MarshalAs(UnmanagedType.LPWStr)] string pszPath);

	private static bool IsConfigurationException(Exception exception)
		=> exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException or Win32Exception;

	internal static IReadOnlyList<string> GetRegistryValueNames(Guid knownFolderId)
	{
		var id = $"{{{knownFolderId.ToString().ToUpperInvariant()}}}";
		if (knownFolderId == Guid.Parse("{FDD39AD0-238F-46AF-ADB4-6C85480369C7}"))
		{
			return new[] { id, "Personal" };
		}

		if (knownFolderId == Guid.Parse("{4BD8D571-6D19-48D3-BE97-422220080E43}"))
		{
			return new[] { id, "My Music" };
		}

		if (knownFolderId == Guid.Parse("{18989B1D-99B5-455B-841C-AB7C74E4DDFC}"))
		{
			return new[] { id, "My Video" };
		}

		return new[] { id };
	}

	private sealed record RegistryValueSnapshot(object Value, RegistryValueKind Kind);
}

#pragma warning restore CA1416
