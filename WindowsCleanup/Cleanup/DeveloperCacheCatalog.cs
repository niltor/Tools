using WindowsCleanup.Localization;

namespace WindowsCleanup.Cleanup;

public static class DeveloperCacheCatalog
{
	public static readonly LocalizedText Category = new("开发缓存", "Developer caches");
	public static readonly LocalizedText BuildCategory = new("项目构建缓存", "Project build caches");
	public static readonly LocalizedText PackageCategory = new("包下载/依赖缓存", "Package download/dependency caches");
	public static readonly LocalizedText ToolCategory = new("开发工具缓存", "Developer tool caches");

	public static IReadOnlyList<CleanupTarget> Build(DeveloperCacheKind kind = DeveloperCacheKind.None)
	{
		var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var targets = new List<CleanupTarget>();
		void Add(string id, string name, params string?[] paths)
		{
			var itemKind = id switch
			{
				"go-build" => DeveloperCacheKind.ProjectBuild,
				"vscode" or "jetbrains" or "android" => DeveloperCacheKind.Tools,
				_ => DeveloperCacheKind.Packages
			};
			var category = itemKind == DeveloperCacheKind.ProjectBuild ? BuildCategory : itemKind == DeveloperCacheKind.Packages ? PackageCategory : ToolCategory;
			targets.Add(new CleanupTarget("dev-" + id, category, new(name, name),
				itemKind == DeveloperCacheKind.Packages
					? new("包下载/依赖缓存，日常构建清理不包含此项；删除后可能需联网重新下载，影响离线构建。", "Package downloads/dependencies, excluded from routine build cleanup; removal may require downloads and affect offline builds.")
					: new("关闭相关工具后清理；缓存会重新生成，保留下载包、SDK、配置和源代码。", "Close related tools first; caches will be regenerated, keeping downloaded packages, SDKs, settings and source code."),
				paths.Where(IsCachePath).Select(path => new CleanupLocation(Path.GetFullPath(path!), NuGetPackageCache: id == "nuget-packages")),
				risk: CleanupRisk.Moderate, optIn: true, developerKind: itemKind));
		}
		string Configured(string variable, string fallback)
			=> Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
				? Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')) : fallback;
		Add("node", "Node.js / npm / Yarn / pnpm",
			Configured("npm_config_cache", Path.Combine(local, "npm-cache")),
			Configured("YARN_CACHE_FOLDER", Path.Combine(local, "Yarn", "Cache")),
			Path.Combine(local, "pnpm", "store"), Path.Combine(home, ".pnpm-store"),
			Path.Combine(local, "node-gyp", "Cache"), Path.Combine(home, ".node-gyp"));
		Add("python", "Python / pip / uv",
			Configured("PIP_CACHE_DIR", Path.Combine(local, "pip", "Cache")),
			Configured("UV_CACHE_DIR", Path.Combine(local, "uv", "cache")));
		Add("nuget-http", ".NET / NuGet HTTP",
			Configured("NUGET_HTTP_CACHE_PATH", Path.Combine(local, "NuGet", "v3-cache")),
			Path.Combine(local, "NuGet", "plugins-cache"));
		Add("nuget-packages", ".NET / NuGet packages",
			Configured("NUGET_PACKAGES", Path.Combine(home, ".nuget", "packages")));
		Add("go-build", "Go build", Configured("GOCACHE", Path.Combine(local, "go-build")));
		var goPaths = Configured("GOPATH", Path.Combine(home, "go")).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
		Add("go-download", "Go module downloads", goPaths.Select(path => Path.Combine(path, "pkg", "mod", "cache", "download"))
			.Append(Environment.GetEnvironmentVariable("GOMODCACHE") is { Length: > 0 } mod ? Path.Combine(mod, "cache", "download") : null).ToArray());
		var cargo = Configured("CARGO_HOME", Path.Combine(home, ".cargo"));
		Add("rust", "Rust / Cargo archives", Path.Combine(cargo, "registry", "cache"));
		var gradle = Configured("GRADLE_USER_HOME", Path.Combine(home, ".gradle"));
		Add("gradle", "Java / Kotlin / Gradle", Path.Combine(gradle, "caches"));
		Add("tools", "Scoop / Chocolatey", Path.Combine(home, "scoop", "cache"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "cache"));
		Add("vscode", "VS Code / Insiders", new[] { local, roaming }.SelectMany(root =>
			new[] { "Code", "Code - Insiders" }.SelectMany(app => new[] { "Cache", "CachedData", "Code Cache", "GPUCache" }
				.Select(cache => Path.Combine(root, app, cache)))).ToArray());
		Add("android", "Android", Path.Combine(home, ".android", "cache"));
		Add("jetbrains", "JetBrains", ProjectCacheDiscovery.Directories(Path.Combine(local, "JetBrains"))
			.Select(path => Path.Combine(path, "caches")).ToArray());
		targets.Add(new CleanupTarget("dev-old-logs", ToolCategory, new("JetBrains 旧日志/临时文件", "JetBrains old logs/temp"),
			new("仅处理超过 30 天的诊断日志和临时文件。", "Only diagnostics and temporary files older than 30 days."),
			ProjectCacheDiscovery.Directories(Path.Combine(local, "JetBrains")).SelectMany(path => new[] { "log", "tmp" }
				.Select(name => new CleanupLocation(Path.Combine(path, name), MinimumAgeDays: 30))), risk: CleanupRisk.Moderate, optIn: true, developerKind: DeveloperCacheKind.Tools));
		targets.Add(new CleanupTarget("dev-conda", PackageCategory, new("Conda 下载归档", "Conda downloaded archives"),
			new("只删除超过 30 天的下载归档，保留解压包和环境。", "Only downloaded archives older than 30 days; extracted packages and environments are kept."),
			(Environment.GetEnvironmentVariable("CONDA_PKGS_DIRS") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
				.Where(IsCachePath).SelectMany(path => new[] { "*.conda", "*.tar.bz2" }.Select(pattern =>
					new CleanupLocation(path, CleanupLocationKind.MatchingFiles, pattern, false, 30))), risk: CleanupRisk.Moderate, optIn: true, developerKind: DeveloperCacheKind.Packages));
		return targets.Where(target => kind == DeveloperCacheKind.None || target.DeveloperKind == kind).ToArray();
	}

	// Environment overrides must not turn a cache rule into a broad personal-data deletion.
	internal static bool IsCachePath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
		try
		{
			var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
			if (full == Path.GetPathRoot(full) || SafeFileSystem.HasReparsePointAncestor(full)) return false;
			var roots = new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData,
				Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonApplicationData,
				Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory,
				Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 };
			if (roots.Select(Environment.GetFolderPath).Where(root => !string.IsNullOrEmpty(root))
				.Any(root => UserFolderDiscovery.IsSameOrDescendant(root, full))) return false;
			return !UserFolderDiscovery.IsSameOrDescendant(full, UserFolderDiscovery.GetDownloadsPath());
		}
		catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
		{
			return false;
		}
	}
}
