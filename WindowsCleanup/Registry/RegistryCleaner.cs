using Microsoft.Win32;
using WindowsCleanup.Localization;

namespace WindowsCleanup.Registry;

public sealed record RegistryCleanupTarget(LocalizedText Name, string Path, LocalizedText Note);

public sealed record RegistryPreview(RegistryCleanupTarget Target, int EntryCount);

public sealed record RegistryOrphanCandidate(
	string Hive,
	string View,
	string KeyPath,
	string DisplayName,
	string InstallLocation);

public sealed record RegistryCleanupResult(int ClearedEntries, int FailedTargets, IReadOnlyList<string> Errors);

public sealed class RegistryCleaner
{
	private static readonly IReadOnlyList<RegistryCleanupTarget> Targets = new[]
	{
		new RegistryCleanupTarget(
			new("运行记录", "Run dialog history"),
			@"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU",
			new("只包含当前用户输入过的运行命令。", "Only commands entered by the current user in the Run dialog.")),
		new RegistryCleanupTarget(
			new("最近打开记录", "Recent-open history"),
			@"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs",
			new("只清理历史记录，不删除实际文件。", "Clears history only; original files are not deleted.")),
		new RegistryCleanupTarget(
			new("资源管理器路径记录", "Explorer path history"),
			@"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths",
			new("清理资源管理器地址栏历史。", "Clears File Explorer address-bar history.")),
		new RegistryCleanupTarget(
			new("搜索记录", "Search history"),
			@"Software\Microsoft\Windows\CurrentVersion\Explorer\WordWheelQuery",
			new("清理资源管理器搜索关键词历史。", "Clears File Explorer search keywords.")),
		new RegistryCleanupTarget(
			new("文件对话框记录", "File-dialog history"),
			@"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU",
			new("清理打开/保存对话框中的最近路径。", "Clears recent paths from Open/Save dialogs.")),
		new RegistryCleanupTarget(
			new("文件对话框访问记录", "File-dialog visit history"),
			@"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\LastVisitedMRU",
			new("清理应用访问文件夹的历史，不删除文件。", "Clears the history of folders visited by applications without deleting files.")),
		new RegistryCleanupTarget(
			new("文件对话框访问路径", "File-dialog visited paths"),
			@"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\LastVisitedPidlMRU",
			new("清理打开/保存对话框访问过的路径。", "Clears paths visited in Open/Save dialogs.")),
		new RegistryCleanupTarget(
			new("文件类型打开记录", "File-type open history"),
			@"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSaveMRU",
			new("清理按文件类型保存的打开/保存历史。", "Clears Open/Save history grouped by file type.")),
		new RegistryCleanupTarget(
			new("最近访问应用记录", "Recent-app history"),
			@"Software\Microsoft\Windows\CurrentVersion\Search\RecentApps",
			new("清理当前用户的应用使用历史。", "Clears current-user app usage history.")),
		new RegistryCleanupTarget(
			new("应用启动记录", "Application launch history"),
			@"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist",
			new("清理资源管理器记录的应用启动历史，不删除应用。", "Clears Explorer's application launch history without deleting applications.")),
		new RegistryCleanupTarget(
			new("兼容性助手历史", "Compatibility Assistant history"),
			@"Software\Microsoft\Windows\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
			new("清理兼容性助手的历史记录，不改变兼容性设置。", "Clears Compatibility Assistant history without changing compatibility settings.")),
		new RegistryCleanupTarget(
			new("应用显示名称缓存", "Application display-name cache"),
			@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
			new("清理可重新生成的应用显示名称缓存。", "Clears the regenerable application display-name cache.")),
		new RegistryCleanupTarget(
			new("7-Zip 压缩历史", "7-Zip archive history"),
			@"Software\7-Zip\Compression\ArcHistory",
			new("清理 7-Zip 最近使用的压缩包路径。", "Clears 7-Zip's recent archive paths.")),
		new RegistryCleanupTarget(
			new("7-Zip 解压路径历史", "7-Zip extraction history"),
			@"Software\7-Zip\Extraction\PathHistory",
			new("清理 7-Zip 最近使用的解压路径。", "Clears 7-Zip's recent extraction paths.")),
		new RegistryCleanupTarget(
			new("7-Zip 文件管理器历史", "7-Zip file-manager history"),
			@"Software\7-Zip\FM\CopyHistory",
			new("清理 7-Zip 文件管理器复制历史。", "Clears 7-Zip file-manager copy history.")),
		new RegistryCleanupTarget(
			new("7-Zip 文件夹历史", "7-Zip folder history"),
			@"Software\7-Zip\FM\FolderHistory",
			new("清理 7-Zip 文件管理器文件夹历史。", "Clears 7-Zip file-manager folder history.")),
		new RegistryCleanupTarget(
			new("WinRAR 压缩历史", "WinRAR archive history"),
			@"Software\WinRAR\ArcHistory",
			new("清理 WinRAR 最近使用的压缩包历史。", "Clears WinRAR's recent archive history.")),
		new RegistryCleanupTarget(
			new("WinRAR 解压历史", "WinRAR extraction history"),
			@"Software\WinRAR\ZipHistory",
			new("清理 WinRAR 最近使用的解压历史。", "Clears WinRAR's recent extraction history.")),
		new RegistryCleanupTarget(
			new("WinRAR 打开记录", "WinRAR open history"),
			@"Software\WinRAR\OpenArc",
			new("清理 WinRAR 最近打开的压缩包记录。", "Clears WinRAR's recently opened archive history.")),
		new RegistryCleanupTarget(
			new("WinRAR 文件夹记录", "WinRAR folder history"),
			@"Software\WinRAR\OpenFolder",
			new("清理 WinRAR 最近访问的文件夹记录。", "Clears WinRAR's recently visited folder history.")),
		new RegistryCleanupTarget(
			new("画图最近文件", "Paint recent files"),
			@"Software\Microsoft\Windows\CurrentVersion\Applets\Paint\Recent File List",
			new("清理画图最近打开文件列表，不删除原文件。", "Clears Paint's recent-file list without deleting the original files.")),
	};

	public IReadOnlyList<RegistryPreview> Preview()
	{
		if (!OperatingSystem.IsWindows())
		{
			return Array.Empty<RegistryPreview>();
		}

		var result = new List<RegistryPreview>();
		foreach (var target in Targets)
		{
			try
			{
				using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(target.Path, writable: false);
				if (key is null)
				{
					continue;
				}

				var count = CountEntries(key, 0, 10_000);
				if (count > 0)
				{
					result.Add(new RegistryPreview(target, count));
				}
			}
			catch (Exception exception) when (IsRegistryException(exception))
			{
				// A locked or unavailable history key is simply omitted from the preview.
			}
		}

		return result;
	}

	public IReadOnlyList<RegistryOrphanCandidate> FindOrphanedUninstallEntries()
	{
		if (!OperatingSystem.IsWindows())
		{
			return Array.Empty<RegistryOrphanCandidate>();
		}

		var result = new List<RegistryOrphanCandidate>();
		foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
		{
			foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
			{
				try
				{
					using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
					using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", writable: false);
					if (uninstall is null)
					{
						continue;
					}

					foreach (var subKeyName in uninstall.GetSubKeyNames())
					{
						try
						{
							using var subKey = uninstall.OpenSubKey(subKeyName, writable: false);
							if (subKey is null || IsProtectedUninstallEntry(subKey))
							{
								continue;
							}

							var keyPath = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{subKeyName}";
							var application = ReadInstalledApplication(subKey, FormatHive(hive), FormatView(view), keyPath);
							if (application is null)
							{
								continue;
							}

							var registeredPath = application.InstallLocation ?? application.ExecutablePath;
							if (string.IsNullOrWhiteSpace(registeredPath) ||
								(application.InstallLocation is not null &&
									(Directory.Exists(application.InstallLocation) || File.Exists(application.InstallLocation))) ||
								(application.ExecutablePath is not null &&
									(File.Exists(application.ExecutablePath) || Directory.Exists(application.ExecutablePath))))
							{
								continue;
							}

							result.Add(new RegistryOrphanCandidate(
								FormatHive(hive),
								FormatView(view),
								keyPath,
								application.DisplayName,
								registeredPath));
						}
						catch (Exception exception) when (IsRegistryException(exception))
						{
							// A malformed uninstall entry must not stop the review scan.
						}
					}
				}
				catch (Exception exception) when (IsRegistryException(exception))
				{
					// Some registry views are unavailable on a particular installation.
				}
			}
		}

		return result
			.GroupBy(candidate => $"{candidate.DisplayName}|{candidate.InstallLocation}", StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.OrderBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
			.ThenBy(candidate => candidate.InstallLocation, StringComparer.OrdinalIgnoreCase)
			.Take(200)
			.ToArray();
	}

	public RegistryCleanupResult Clear(IEnumerable<RegistryPreview> previews)
	{
		if (!OperatingSystem.IsWindows())
		{
			return new RegistryCleanupResult(0, 1, new[] { "The registry is only available on Windows." });
		}

		var clearedEntries = 0;
		var failedTargets = 0;
		var errors = new List<string>();

		foreach (var preview in previews)
		{
			try
			{
				using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(preview.Target.Path, writable: true);
				if (key is null)
				{
					continue;
				}

				clearedEntries += ClearContents(key, preview.Target.Path, errors);
			}
			catch (Exception exception) when (IsRegistryException(exception))
			{
				failedTargets++;
				if (errors.Count < 5)
				{
					errors.Add($"{preview.Target.Path}: {exception.Message}");
				}
			}
		}

		return new RegistryCleanupResult(clearedEntries, failedTargets, errors);
	}

	internal static IReadOnlyList<InstalledApplication> GetInstalledApplications()
	{
		if (!OperatingSystem.IsWindows())
		{
			return Array.Empty<InstalledApplication>();
		}

		var result = new List<InstalledApplication>();
		foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
		{
			foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
			{
				try
				{
					using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
					using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", writable: false);
					if (uninstall is null)
					{
						continue;
					}

					foreach (var subKeyName in uninstall.GetSubKeyNames())
					{
						try
						{
							using var subKey = uninstall.OpenSubKey(subKeyName, writable: false);
							if (subKey is null || IsProtectedUninstallEntry(subKey))
							{
								continue;
							}

							var keyPath = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{subKeyName}";
							var application = ReadInstalledApplication(subKey, FormatHive(hive), FormatView(view), keyPath);
							if (application is not null)
							{
								result.Add(application);
							}
						}
						catch (Exception exception) when (IsRegistryException(exception))
						{
							// One malformed uninstall entry must not stop application discovery.
						}
					}
				}
				catch (Exception exception) when (IsRegistryException(exception))
				{
					// Some registry views are unavailable on a particular Windows installation.
				}
			}
		}

		return result
			.GroupBy(application =>
				$"{NormalizeName(application.DisplayName)}|{application.InstallLocation}|{application.ExecutablePath}",
				StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.OrderBy(application => application.DisplayName, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	internal static string NormalizeName(string value)
		=> new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

	private static InstalledApplication? ReadInstalledApplication(
		RegistryKey key,
		string hive,
		string view,
		string keyPath)
	{
		var displayName = GetStringValue(key, "DisplayName")?.Trim();
		if (string.IsNullOrWhiteSpace(displayName))
		{
			return null;
		}

		var installLocation = NormalizeInstallLocation(GetStringValue(key, "InstallLocation"));
		var executablePath = NormalizeRegisteredExecutablePath(GetStringValue(key, "DisplayIcon"));
		if (installLocation is null && executablePath is not null)
		{
			installLocation = Path.GetDirectoryName(executablePath);
		}

		return new InstalledApplication(
			displayName,
			GetStringValue(key, "Publisher")?.Trim(),
			GetStringValue(key, "DisplayVersion")?.Trim(),
			installLocation,
			executablePath,
			GetStringValue(key, "UninstallString")?.Trim(),
			hive,
			view,
			keyPath);
	}

	private static string? GetStringValue(RegistryKey key, string valueName)
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}

		return key.GetValue(valueName)?.ToString();
	}

	private static string? NormalizeRegisteredExecutablePath(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		try
		{
			var normalized = Environment.ExpandEnvironmentVariables(value.Trim());
			var executableEnd = normalized.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
			string path;
			if (executableEnd >= 0)
			{
				path = normalized[..(executableEnd + 4)].Trim().Trim('"');
			}
			else if (normalized.StartsWith('"'))
			{
				var closingQuote = normalized.IndexOf('"', 1);
				path = (closingQuote > 1 ? normalized[1..closingQuote] : normalized).Trim();
			}
			else
			{
				path = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim('"');
			}

			if (!Path.IsPathRooted(path) ||
				Path.GetFileName(path).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			return Path.GetFullPath(path);
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return null;
		}
	}

	private static bool IsProtectedUninstallEntry(RegistryKey key)
	{
		if (!OperatingSystem.IsWindows())
		{
			return true;
		}

		static bool IsEnabled(object? value)
			=> value switch
		{
			int number => number != 0,
			long number => number != 0,
			string text when int.TryParse(text, out var number) => number != 0,
			_ => false
		};

		if (IsEnabled(key.GetValue("SystemComponent")) || IsEnabled(key.GetValue("NoRemove")))
		{
			return true;
		}

		if (key.GetValue("ParentKeyName") is string parent && !string.IsNullOrWhiteSpace(parent))
		{
			return true;
		}

		var releaseType = key.GetValue("ReleaseType")?.ToString();
		return releaseType is not null &&
			(releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
			 releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase) ||
			 releaseType.Contains("Security", StringComparison.OrdinalIgnoreCase));
	}

	private static string? NormalizeInstallLocation(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		try
		{
			var normalized = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
			return Path.IsPathRooted(normalized) ? Path.GetFullPath(normalized) : null;
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return null;
		}
	}

	private static string FormatHive(RegistryHive hive)
		=> hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";

	private static string FormatView(RegistryView view)
		=> view == RegistryView.Registry64 ? "64-bit" : "32-bit";

	private static int CountEntries(RegistryKey key, int depth, int remaining)
	{
		if (!OperatingSystem.IsWindows())
		{
			return 0;
		}

		if (depth > 16 || remaining <= 0)
		{
			return 0;
		}

		var count = key.ValueCount;
		remaining -= count;
		if (remaining <= 0)
		{
			return count;
		}

		foreach (var subKeyName in key.GetSubKeyNames())
		{
			if (remaining <= 0)
			{
				break;
			}

			try
			{
				using var subKey = key.OpenSubKey(subKeyName, writable: false);
				if (subKey is null)
				{
					continue;
				}

				var subCount = CountEntries(subKey, depth + 1, remaining);
				count += subCount;
				remaining -= subCount;
			}
			catch (Exception exception) when (IsRegistryException(exception))
			{
				// Skip a single inaccessible subkey.
			}
		}

		return count;
	}

	private static int ClearContents(RegistryKey key, string path, ICollection<string> errors)
	{
		if (!OperatingSystem.IsWindows())
		{
			return 0;
		}

		var cleared = 0;
		foreach (var valueName in key.GetValueNames())
		{
			try
			{
				key.DeleteValue(valueName, throwOnMissingValue: false);
				cleared++;
			}
			catch (Exception exception) when (IsRegistryException(exception))
			{
				if (errors.Count < 5)
				{
					errors.Add($"{path}\\{valueName}: {exception.Message}");
				}
			}
		}

		foreach (var subKeyName in key.GetSubKeyNames())
		{
			try
			{
				var subCount = 0;
				using (var subKey = key.OpenSubKey(subKeyName, writable: false))
				{
					if (subKey is not null)
					{
						subCount = CountEntries(subKey, 0, 10_000);
					}
				}

				key.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
				cleared += subCount;
			}
			catch (Exception exception) when (IsRegistryException(exception))
			{
				if (errors.Count < 5)
				{
					errors.Add($"{path}\\{subKeyName}: {exception.Message}");
				}
			}
		}

		return cleared;
	}

	private static bool IsRegistryException(Exception exception)
		=> exception is UnauthorizedAccessException or IOException or System.Security.SecurityException or ObjectDisposedException or ArgumentException;
}
