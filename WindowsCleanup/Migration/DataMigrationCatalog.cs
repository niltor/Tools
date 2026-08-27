#pragma warning disable CA1416

using System.Diagnostics;
using Microsoft.Win32;
using WindowsCleanup.Cleanup;
using WindowsCleanup.Localization;

namespace WindowsCleanup.Migration;

internal static class DataMigrationCatalog
{
	private static readonly LocalizedText UserFolderCategory = new("用户文件夹", "User folders");
	private static readonly LocalizedText DeveloperCategory = new("开发工具目录", "Developer tool directories");
	private static readonly LocalizedText ApplicationCategory = new("软件数据", "Application data");

	public static IReadOnlyList<MigrationTarget> FindAvailable()
	{
		if (!OperatingSystem.IsWindows())
		{
			return Array.Empty<MigrationTarget>();
		}

		var targets = new List<MigrationTarget>();
		AddKnownFolder(
			targets,
			"documents",
			new("Documents（文档）", "Documents"),
			UserFolderDiscovery.GetDocumentsPath(),
			"{FDD39AD0-238F-46AF-ADB4-6C85480369C7}");
		AddKnownFolder(
			targets,
			"downloads",
			new("Downloads（下载）", "Downloads"),
			UserFolderDiscovery.GetDownloadsPath(),
			"{374DE290-123F-4565-9164-39C4925E467B}");
		AddKnownFolder(
			targets,
			"music",
			new("Music（音乐）", "Music"),
			UserFolderDiscovery.GetMusicPath(),
			"{4BD8D571-6D19-48D3-BE97-422220080E43}");
		AddKnownFolder(
			targets,
			"videos",
			new("Videos（视频）", "Videos"),
			UserFolderDiscovery.GetVideosPath(),
			"{18989B1D-99B5-455B-841C-AB7C74E4DDFC}");

		var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		AddEnvironmentTarget(
			targets, "nuget-global", new("NuGet 全局包", "NuGet global packages"), DeveloperCategory,
			"NUGET_PACKAGES", ConfiguredOrDefault("NUGET_PACKAGES", Combine(profile, ".nuget", "packages")),
			new("复制包目录后设置用户环境变量 NUGET_PACKAGES。仅迁移包，不修改项目文件。", "Copy the package directory and set the user NUGET_PACKAGES environment variable; project files are not changed."),
			new("设置用户环境变量 NUGET_PACKAGES", "Set the user NUGET_PACKAGES environment variable"),
			new[] { "dotnet" });
		AddEnvironmentTarget(
			targets, "nuget-http", new("NuGet HTTP 缓存", "NuGet HTTP cache"), DeveloperCategory,
			"NUGET_HTTP_CACHE_PATH", ConfiguredOrDefault("NUGET_HTTP_CACHE_PATH", Combine(local, "NuGet", "v3-cache")),
			new("复制 NuGet HTTP 缓存后设置 NUGET_HTTP_CACHE_PATH；缓存丢失时 NuGet 会重新获取。", "Copy the NuGet HTTP cache and set NUGET_HTTP_CACHE_PATH; NuGet can fetch missing cache entries again."),
			new("设置用户环境变量 NUGET_HTTP_CACHE_PATH", "Set the user NUGET_HTTP_CACHE_PATH environment variable"),
			new[] { "dotnet" });
		AddEnvironmentTarget(
			targets, "nuget-plugins", new("NuGet 插件缓存", "NuGet plugin cache"), DeveloperCategory,
			"NUGET_PLUGINS_CACHE_PATH", ConfiguredOrDefault("NUGET_PLUGINS_CACHE_PATH", Combine(local, "NuGet", "plugins-cache")),
			new("复制 NuGet 插件缓存后设置 NUGET_PLUGINS_CACHE_PATH。", "Copy the NuGet plugin cache and set NUGET_PLUGINS_CACHE_PATH."),
			new("设置用户环境变量 NUGET_PLUGINS_CACHE_PATH", "Set the user NUGET_PLUGINS_CACHE_PATH environment variable"),
			new[] { "dotnet" });

		var npmDefault = Combine(local, "npm-cache");
		AddEnvironmentTarget(
			targets, "npm-cache", new("npm 缓存", "npm cache"), DeveloperCategory,
			"npm_config_cache", ResolveNpmCachePath(npmDefault),
			new("复制 npm 缓存后设置用户环境变量 npm_config_cache；npm 缓存内容可以重新获取。", "Copy the npm cache and set the user npm_config_cache environment variable; npm can fetch missing cache content again."),
			new("设置用户环境变量 npm_config_cache", "Set the user npm_config_cache environment variable"),
			new[] { "node", "npm" });

		AddEnvironmentTarget(
			targets, "yarn-cache", new("Yarn 缓存", "Yarn cache"), DeveloperCategory,
			"YARN_CACHE_FOLDER", ConfiguredOrDefault("YARN_CACHE_FOLDER", FirstExisting(
				Combine(local, "Yarn", "Cache"),
				Combine(local, "Yarn", "Berry", "Cache"))),
			new("复制 Yarn 缓存后设置 YARN_CACHE_FOLDER；项目内 .yarnrc.yml 的 cacheFolder 不会被自动改写。", "Copy the Yarn cache and set YARN_CACHE_FOLDER; project-local cacheFolder in .yarnrc.yml is not rewritten automatically."),
			new("设置用户环境变量 YARN_CACHE_FOLDER", "Set the user YARN_CACHE_FOLDER environment variable"),
			new[] { "node", "yarn" });

		AddEnvironmentTarget(
			targets, "pnpm-store", new("pnpm Store", "pnpm store"), DeveloperCategory,
			"", TryGetPnpmStorePath(),
			new("迁移 pnpm store，并使用 pnpm config set store-dir --global 更新全局配置。", "Migrate the pnpm store and update the global setting with pnpm config set store-dir."),
			new("执行 pnpm config set store-dir <新路径> --global", "Run pnpm config set store-dir <new path> --global"),
			new[] { "node", "pnpm" }, MigrationConfigurationKind.PnpmStore);

		AddEnvironmentTarget(
			targets, "pip-cache", new("pip 缓存", "pip cache"), DeveloperCategory,
			"PIP_CACHE_DIR", ConfiguredOrDefault("PIP_CACHE_DIR", Combine(local, "pip", "Cache")),
			new("复制 pip 缓存后设置 PIP_CACHE_DIR；缺少的 wheel/源码仍可重新下载。", "Copy the pip cache and set PIP_CACHE_DIR; missing wheels or source archives can be downloaded again."),
			new("设置用户环境变量 PIP_CACHE_DIR", "Set the user PIP_CACHE_DIR environment variable"),
			new[] { "python", "pip" });
		AddEnvironmentTarget(
			targets, "uv-cache", new("uv 缓存", "uv cache"), DeveloperCategory,
			"UV_CACHE_DIR", ConfiguredOrDefault("UV_CACHE_DIR", Combine(local, "uv", "cache")),
			new("复制完整 uv 缓存后设置 UV_CACHE_DIR；不直接拆分或删除缓存条目。", "Copy the complete uv cache and set UV_CACHE_DIR; cache entries are not edited individually."),
			new("设置用户环境变量 UV_CACHE_DIR", "Set the user UV_CACHE_DIR environment variable"),
			new[] { "uv", "python" });

		var goRoot = Combine(profile, "go");
		AddEnvironmentTarget(
			targets, "go-mod-cache", new("Go 模块缓存", "Go module cache"), DeveloperCategory,
			"GOMODCACHE", ConfiguredOrDefault("GOMODCACHE", Combine(goRoot, "pkg", "mod")),
			new("复制 Go 模块缓存后设置 GOMODCACHE；只迁移模块缓存，不迁移整个 GOPATH。", "Copy the Go module cache and set GOMODCACHE; the entire GOPATH is not moved."),
			new("设置用户环境变量 GOMODCACHE", "Set the user GOMODCACHE environment variable"),
			new[] { "go" });
		AddEnvironmentTarget(
			targets, "go-build-cache", new("Go 编译缓存", "Go build cache"), DeveloperCategory,
			"GOCACHE", ConfiguredOrDefault("GOCACHE", Combine(local, "go-build")),
			new("复制 Go 编译缓存后设置 GOCACHE；缓存可由 Go 重新生成。", "Copy the Go build cache and set GOCACHE; Go can regenerate the cache."),
			new("设置用户环境变量 GOCACHE", "Set the user GOCACHE environment variable"),
			new[] { "go" });
		AddEnvironmentTarget(
			targets, "pub-cache", new("Dart/Flutter Pub 缓存", "Dart/Flutter Pub cache"), DeveloperCategory,
			"PUB_CACHE", ConfiguredOrDefault("PUB_CACHE", Combine(local, "Pub", "Cache")),
			new("复制 Pub 缓存后设置 PUB_CACHE；项目依赖声明不会被修改。", "Copy the Pub cache and set PUB_CACHE; project dependency declarations are not changed."),
			new("设置用户环境变量 PUB_CACHE", "Set the user PUB_CACHE environment variable"),
			new[] { "dart", "flutter" });
		AddEnvironmentTarget(
			targets, "poetry-cache", new("Poetry 缓存", "Poetry cache"), DeveloperCategory,
			"POETRY_CACHE_DIR", ConfiguredOrDefault("POETRY_CACHE_DIR", Combine(local, "pypoetry", "Cache")),
			new("复制 Poetry 缓存后设置 POETRY_CACHE_DIR；项目虚拟环境不会随此项迁移。", "Copy the Poetry cache and set POETRY_CACHE_DIR; project virtual environments are not moved by this item."),
			new("设置用户环境变量 POETRY_CACHE_DIR", "Set the user POETRY_CACHE_DIR environment variable"),
			new[] { "python", "poetry" });
		AddEnvironmentTarget(
			targets, "composer-cache", new("Composer 缓存", "Composer cache"), DeveloperCategory,
			"COMPOSER_CACHE_DIR", ConfiguredOrDefault("COMPOSER_CACHE_DIR", Combine(local, "Composer")),
			new("复制 Composer 缓存后设置 COMPOSER_CACHE_DIR；不会迁移项目 vendor 目录。", "Copy the Composer cache and set COMPOSER_CACHE_DIR; project vendor directories are not moved."),
			new("设置用户环境变量 COMPOSER_CACHE_DIR", "Set the user COMPOSER_CACHE_DIR environment variable"),
			new[] { "php", "composer" });
		AddEnvironmentTarget(
			targets, "vcpkg-binary-cache", new("vcpkg 二进制缓存", "vcpkg binary cache"), DeveloperCategory,
			"VCPKG_DEFAULT_BINARY_CACHE", ConfiguredOrDefault("VCPKG_DEFAULT_BINARY_CACHE", Combine(local, "vcpkg", "archives")),
			new("复制 vcpkg 二进制缓存后设置 VCPKG_DEFAULT_BINARY_CACHE；不会移动 vcpkg 源码树。", "Copy the vcpkg binary cache and set VCPKG_DEFAULT_BINARY_CACHE; the vcpkg source tree is not moved."),
			new("设置用户环境变量 VCPKG_DEFAULT_BINARY_CACHE", "Set the user VCPKG_DEFAULT_BINARY_CACHE environment variable"),
			new[] { "vcpkg" });
		AddEnvironmentTarget(
			targets, "cargo-home", new("Cargo 用户目录（仅显式配置）", "Cargo home (explicit configuration only)"), DeveloperCategory,
			"CARGO_HOME", ResolveEnvironmentPath("CARGO_HOME"),
			new("仅当 CARGO_HOME 已显式设置时迁移完整 Cargo 用户目录；其中可能包含配置和凭据，请逐项确认。", "Only migrate the complete Cargo home when CARGO_HOME is explicitly set; it may contain configuration and credentials, so review it carefully."),
			new("设置用户环境变量 CARGO_HOME", "Set the user CARGO_HOME environment variable"),
			new[] { "cargo", "rustc" });
		AddEnvironmentTarget(
			targets, "gradle-user-home", new("Gradle 用户目录（仅显式配置）", "Gradle user home (explicit configuration only)"), DeveloperCategory,
			"GRADLE_USER_HOME", ResolveEnvironmentPath("GRADLE_USER_HOME"),
			new("仅当 GRADLE_USER_HOME 已显式设置时迁移；该目录可能包含 init 脚本、配置和凭据。", "Only migrate an explicitly configured GRADLE_USER_HOME; it may contain init scripts, configuration, and credentials."),
			new("设置用户环境变量 GRADLE_USER_HOME", "Set the user GRADLE_USER_HOME environment variable"),
			new[] { "gradle", "java" });

		var wechatPath = GetWeChatConfiguredStoragePath();
		if (wechatPath is not null)
		{
			var wechatTarget = new MigrationTarget(
				"wechat-data",
				ApplicationCategory,
				new("微信已配置文件存储", "WeChat configured file storage"),
				wechatPath,
				new("复制微信当前 FileSavePath 指向的完整数据目录，不拆分聊天数据库、媒体或附件。", "Copy the complete directory currently referenced by WeChat FileSavePath; chat databases, media, and attachments are not split."),
				new("复制成功后更新 HKCU\\Software\\Tencent\\WeChat 的 FileSavePath", "After copying, update FileSavePath under HKCU\\Software\\Tencent\\WeChat"),
				new MigrationConfiguration(
					MigrationConfigurationKind.RegistryValue,
					RegistryPath: @"Software\Tencent\WeChat",
					ValueName: "FileSavePath"),
				new[] { "WeChat", "Weixin" });
			AddTarget(targets, wechatTarget);
		}

		AddEnvironmentTarget(
			targets, "codex-home", new("Codex CLI 数据（仅 CODEX_HOME）", "Codex CLI data (CODEX_HOME only)"), ApplicationCategory,
			"CODEX_HOME", ResolveEnvironmentPath("CODEX_HOME"),
			new("仅当用户已经设置 CODEX_HOME 时提供；复制后更新用户环境变量 CODEX_HOME。不会猜测桌面 Codex 应用的内部目录。", "Offered only when CODEX_HOME is already set; copy it and update the user CODEX_HOME environment variable. The desktop Codex app's internal directory is not guessed."),
			new("设置用户环境变量 CODEX_HOME；重启 CLI/终端后生效", "Set user CODEX_HOME; restart the CLI/terminal to apply it"),
			new[] { "codex" });
		AddEnvironmentTarget(
			targets, "huggingface-home", new("Hugging Face 模型与缓存", "Hugging Face models and cache"), ApplicationCategory,
			"HF_HOME", ConfiguredOrDefault("HF_HOME", Combine(profile, ".cache", "huggingface")),
			new("复制 HF_HOME 下的模型和缓存后设置 HF_HOME；这是用户主动选择的数据迁移，不在清理功能中删除。", "Copy models and cache under HF_HOME and set HF_HOME; this is opt-in migration data and is never removed by cleanup."),
			new("设置用户环境变量 HF_HOME", "Set the user HF_HOME environment variable"),
			new[] { "python" });
		AddEnvironmentTarget(
			targets, "ollama-models", new("Ollama 模型", "Ollama models"), ApplicationCategory,
			"OLLAMA_MODELS", ConfiguredOrDefault("OLLAMA_MODELS", Combine(profile, ".ollama", "models")),
			new("复制 Ollama 模型目录后设置 OLLAMA_MODELS；不会修改 Ollama 服务或删除模型。", "Copy the Ollama model directory and set OLLAMA_MODELS; the Ollama service is not changed and models are not deleted."),
			new("设置用户环境变量 OLLAMA_MODELS；重启 Ollama 后生效", "Set user OLLAMA_MODELS; restart Ollama to apply it"),
			new[] { "ollama" });

		return targets
			.GroupBy(target => target.SourcePath, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.OrderBy(target => target.Category.Chinese, StringComparer.OrdinalIgnoreCase)
			.ThenBy(target => target.Name.Chinese, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static void AddKnownFolder(
		ICollection<MigrationTarget> targets,
		string id,
		LocalizedText name,
		string? path,
		string knownFolderId)
	{
		AddTarget(
			targets,
			path is null ? null : new MigrationTarget(
				$"user-{id}",
				UserFolderCategory,
				name,
				path,
				new("复制目录后更新 Windows 已知文件夹路径；文件夹中的全部用户文件会随目录迁移。", "Copy the directory and update the Windows known-folder path; all user files in the folder move with it."),
				new("更新 User Shell Folders/Shell Folders，并调用 SHSetKnownFolderPath", "Update User Shell Folders/Shell Folders and call SHSetKnownFolderPath"),
				new MigrationConfiguration(
					MigrationConfigurationKind.KnownFolder,
					KnownFolderId: Guid.Parse(knownFolderId)),
				Array.Empty<string>()));
	}

	private static void AddEnvironmentTarget(
		ICollection<MigrationTarget> targets,
		string id,
		LocalizedText name,
		LocalizedText category,
		string environmentVariable,
		string? sourcePath,
		LocalizedText plan,
		LocalizedText configurationPlan,
		IReadOnlyList<string> processNames,
		MigrationConfigurationKind kind = MigrationConfigurationKind.UserEnvironment)
	{
		if (string.IsNullOrWhiteSpace(sourcePath))
		{
			return;
		}

		AddTarget(
			targets,
			new MigrationTarget(
				id,
				category,
				name,
				sourcePath,
				plan,
				configurationPlan,
				new MigrationConfiguration(kind, Name: environmentVariable),
				processNames));
	}

	private static void AddTarget(ICollection<MigrationTarget> targets, MigrationTarget? target)
	{
		if (target is null || !IsSafeSource(target.SourcePath))
		{
			return;
		}

		if (targets.Any(existing => string.Equals(existing.SourcePath, target.SourcePath, StringComparison.OrdinalIgnoreCase)))
		{
			return;
		}

		var fullPath = Path.GetFullPath(target.SourcePath);
		if (!DataMigrationService.TryGetDirectorySize(fullPath, out var bytes))
		{
			return;
		}

		targets.Add(target with { SourcePath = fullPath, Bytes = bytes });
	}

	private static bool IsSafeSource(string path)
	{
		try
		{
			var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (!Directory.Exists(fullPath) || SafeFileSystem.IsReparsePoint(fullPath))
			{
				return false;
			}

			var root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return !string.IsNullOrWhiteSpace(root) &&
				!string.IsNullOrWhiteSpace(systemRoot) &&
				string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase) &&
				!string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (IsPathException(exception))
		{
			return false;
		}
	}

	private static string? ResolveEnvironmentPath(string variableName)
	{
		try
		{
			return NormalizePath(Environment.GetEnvironmentVariable(variableName));
		}
		catch (Exception exception) when (IsPathException(exception))
		{
			return null;
		}
	}

	private static string? ResolveNpmCachePath(string? fallback)
	{
		var configured = ResolveEnvironmentPath("npm_config_cache") ?? ResolveEnvironmentPath("NPM_CONFIG_CACHE");
		if (configured is not null)
		{
			return configured;
		}

		var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var npmrc = Combine(profile, ".npmrc");
		var fromConfig = ReadSimpleConfigValue(npmrc, "cache");
		return NormalizePath(fromConfig) ?? fallback;
	}

	private static string? GetWeChatConfiguredStoragePath()
	{
		try
		{
			using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Tencent\WeChat", writable: false);
			var value = key?.GetValue("FileSavePath")?.ToString()?.Trim().Trim('"');
			if (string.IsNullOrWhiteSpace(value) || value.Equals("MyDocument:", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			return NormalizePath(value);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
		{
			return null;
		}
	}

	private static string? TryGetPnpmStorePath()
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}

		try
		{
			using var process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = "pnpm.cmd",
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
				}
			};
			process.StartInfo.ArgumentList.Add("store");
			process.StartInfo.ArgumentList.Add("path");
			if (!process.Start() || !process.WaitForExit(5000))
			{
				if (!process.HasExited)
				{
					process.Kill(entireProcessTree: true);
				}
				return null;
			}

			var lines = process.StandardOutput.ReadToEnd()
				.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			return lines
				.Reverse()
				.Select(NormalizePath)
				.FirstOrDefault(path => path is not null);
		}
		catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static string? ReadSimpleConfigValue(string? path, string keyName)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			return null;
		}

		try
		{
			foreach (var line in File.ReadLines(path))
			{
				var trimmed = line.Trim();
				if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
				{
					continue;
				}

				var separator = trimmed.IndexOf('=');
				if (separator <= 0 || !trimmed[..separator].Trim().Equals(keyName, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				return trimmed[(separator + 1)..].Trim().Trim('"');
			}
 		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			// An unreadable optional config falls back to the documented default.
		}

		return null;
	}

	private static string? FirstExisting(params string?[] paths)
		=> paths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));

	private static string? ConfiguredOrDefault(string variableName, string? fallback)
	{
		var configured = Environment.GetEnvironmentVariable(variableName);
		return string.IsNullOrWhiteSpace(configured) ? fallback : ResolveEnvironmentPath(variableName);
	}

	private static string? NormalizePath(string? path)
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
		catch (Exception exception) when (IsPathException(exception))
		{
			return null;
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
		catch (Exception exception) when (IsPathException(exception))
		{
			return null;
		}
	}

	private static bool IsPathException(Exception exception)
		=> exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException;
}

#pragma warning restore CA1416
