using WindowsCleanup.Localization;

namespace WindowsCleanup.Cleanup;

public static class CleanupCatalog
{
	private static readonly LocalizedText DiskCategory = new("C 盘 / 临时文件", "C: / temporary files");
	private static readonly LocalizedText CacheCategory = new("缓存", "Cache");
	private static readonly LocalizedText PrivacyCategory = new("使用痕迹", "Usage traces");
	private static readonly LocalizedText DeveloperCategory = new("开发工具缓存", "Developer caches");
	private static readonly LocalizedText AiCategory = new("AI 工具缓存", "AI tool caches");
	private static readonly LocalizedText MediaCategory = new("视频/直播软件", "Video/live apps");
	private static readonly LocalizedText Regenerable = new("可重新生成，安全删除", "Regenerable and safe to delete");

	public static IReadOnlyList<CleanupTarget> Build()
	{
		var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
		var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
		var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
		var systemRoot = GetSystemDriveRoot(windows);
		var targets = new List<CleanupTarget>();

		Add(targets, "user-temp", DiskCategory,
			new("用户临时文件", "User temporary files"),
			new("当前用户临时文件，可被正在运行的程序占用。", "Current-user temporary files; running programs may keep some files open."),
			Locations(Path.GetTempPath()), false);

		Add(targets, "windows-temp", DiskCategory,
			new("Windows 临时文件", "Windows temporary files"),
			new("只删除可访问的临时文件，不删除目录本身。", "Only accessible temporary files are removed; the directory itself is kept."),
			Locations(Combine(windows, "Temp")), true);

		Add(targets, "windows-update-download", DiskCategory,
			new("Windows 更新下载缓存", "Windows Update download cache"),
			new("只清理已下载的更新缓存，不碰系统组件存储。", "Only downloaded update cache is removed; the component store is untouched."),
			Locations(Combine(windows, "SoftwareDistribution", "Download")), true, CleanupRisk.Moderate, optIn: true);

		Add(targets, "windows-downloaded-program-files", DiskCategory,
			new("Windows 下载的程序文件", "Windows downloaded program files"),
			new("只处理 Windows 的旧下载程序文件；不会删除系统组件。Windows 会在需要时重新下载。", "Only old downloaded program files are considered; system components are untouched and Windows can download them again."),
			OldLocations(30, Combine(windows, "Downloaded Program Files")), true, CleanupRisk.Moderate, optIn: true);

		Add(targets, "delivery-optimization", DiskCategory,
			new("Delivery Optimization 缓存", "Delivery Optimization cache"),
			Regenerable,
			Locations(
				Combine(programData, "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
				Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization")),
			true, CleanupRisk.Moderate, optIn: true);

		Add(targets, "windows-font-cache", CacheCategory,
			new("Windows 字体缓存", "Windows font cache"),
			new("字体缓存可重建，但清理后 Windows 可能重新生成缓存；锁定文件会跳过。", "The font cache is rebuildable, but Windows may rebuild it after cleanup; locked files are skipped."),
			Locations(
				Combine(windows, "ServiceProfiles", "LocalService", "AppData", "Local", "FontCache"),
				Combine(windows, "System32", "FNTCACHE.DAT")),
			true, CleanupRisk.Moderate, optIn: true);

		var windowsSetupLogs = OldMatchingLocations(30,
			Combine(windows, "Panther"),
			true,
			"*.log", "*.etl")
			.Concat(OldMatchingLocations(30,
				Combine(windows, "Logs", "MoSetup"),
				true,
				"*.log", "*.etl"))
			.Concat(OldMatchingLocations(30,
				Combine(windows, "Inf"),
				false,
				"setupapi*.log", "setupapi*.etl"))
			.ToArray();
		Add(targets, "windows-setup-logs", DiskCategory,
			new("Windows 安装诊断日志", "Windows setup diagnostics"),
			new("只处理超过 30 天的 Panther、MoSetup 和 setupapi 诊断日志，不处理安装配置或系统组件。", "Only Panther, MoSetup, and setupapi diagnostic logs older than 30 days are considered; setup configuration and system components are untouched."),
			windowsSetupLogs, true, CleanupRisk.Moderate, optIn: true);

		var memoryDumpLocations = OldMatchingLocations(30,
			Combine(windows, "Minidump"),
			true,
			"*.dmp")
			.Concat(OldMatchingLocations(30,
				Combine(windows, "MEMORY.DMP"),
				false,
				"MEMORY.DMP"))
			.ToArray();
		Add(targets, "system-memory-dumps", DiskCategory,
			new("系统内存转储", "System memory dumps"),
			new("只处理超过 30 天的 MEMORY.DMP/Minidump 诊断文件；删除后无法用于故障分析。", "Only MEMORY.DMP/Minidump diagnostics older than 30 days are considered; deleting them removes evidence useful for troubleshooting."),
			memoryDumpLocations, true, CleanupRisk.Moderate, optIn: true);

		Add(targets, "recycle-bin", DiskCategory,
			new("C: 回收站", "C: recycle bin"),
			new("清空 C: 回收站；回收站中的文件将无法通过回收站恢复。", "Empties the C: recycle bin; those files can no longer be restored from the bin."),
			Locations(systemRoot, CleanupLocationKind.RecycleBin, recursive: false), false, CleanupRisk.Moderate, optIn: true);

		Add(targets, "wer-queues", DiskCategory,
			new("Windows 错误报告队列", "Windows Error Reporting queues"),
			Regenerable,
			Locations(
				Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"),
				Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
				Combine(local, "Microsoft", "Windows", "WER", "ReportQueue"),
				Combine(local, "Microsoft", "Windows", "WER", "ReportArchive")), true);

		Add(targets, "windows-network-cache", CacheCategory,
			new("Windows 网络缓存", "Windows network cache"),
			Regenerable,
			Locations(Combine(local, "Microsoft", "Windows", "INetCache")), false);

		Add(targets, "windows-webcache", CacheCategory,
			new("Windows WebCache 数据", "Windows WebCache data"),
			new("只清理可访问的 WebCache 文件；正在使用的数据库会跳过。", "Only accessible WebCache files are removed; databases in use are skipped."),
			Locations(Combine(local, "Microsoft", "Windows", "WebCache")), false, CleanupRisk.Moderate, optIn: true);

		Add(targets, "thumbnail-cache", CacheCategory,
			new("缩略图缓存", "Thumbnail cache"),
			Regenerable,
			Locations(Combine(local, "Microsoft", "Windows", "Explorer"), CleanupLocationKind.MatchingFiles, "thumbcache*.db", recursive: false), false);

		Add(targets, "shader-caches", CacheCategory,
			new("图形/驱动着色器缓存", "Graphics/driver shader caches"),
			Regenerable,
			Locations(
				Combine(local, "D3DSCache"),
				Combine(local, "NVIDIA", "DXCache"),
				Combine(local, "NVIDIA", "GLCache"),
				Combine(local, "NVIDIA Corporation", "NV_Cache"),
				Combine(programData, "NVIDIA Corporation", "NV_Cache"),
				Combine(local, "AMD", "DxCache"),
				Combine(local, "AMD", "GLCache"),
				Combine(local, "Intel", "ShaderCache")), false);

		Add(targets, "recent-items", PrivacyCategory,
			new("最近使用记录和跳转列表", "Recent items and jump lists"),
			new("清理当前用户的文件使用痕迹，不影响原文件。", "Clears current-user usage traces without touching the original files."),
			Locations(Combine(roaming, "Microsoft", "Windows", "Recent")), false);

		Add(targets, "crash-dumps", PrivacyCategory,
			new("用户崩溃转储", "User crash dumps"),
			Regenerable,
			Locations(Combine(local, "CrashDumps")), false);

		var browserCacheSuffixes = new[]
		{
			"Cache",
			"Code Cache",
			"GPUCache",
			"Media Cache",
			"Application Cache",
			Path.Combine("Service Worker", "CacheStorage"),
			"GraphiteDawnCache",
			"GrShaderCache",
			"ShaderCache",
			"component_crx_cache",
			"extensions_crx_cache"
		};

		var chromeRoot = Combine(local, "Google", "Chrome", "User Data");
		var browserLocations = ProfileCacheLocations(chromeRoot, browserCacheSuffixes)
			.Concat(BrowserTransientLocations(chromeRoot))
			.ToArray();
		Add(targets, "chrome-cache", CacheCategory,
			new("Google Chrome 缓存", "Google Chrome cache"),
			Regenerable, browserLocations);

		var edgeRoot = Combine(local, "Microsoft", "Edge", "User Data");
		browserLocations = ProfileCacheLocations(edgeRoot, browserCacheSuffixes)
			.Concat(BrowserTransientLocations(edgeRoot))
			.ToArray();
		Add(targets, "edge-cache", CacheCategory,
			new("Microsoft Edge 缓存", "Microsoft Edge cache"),
			Regenerable, browserLocations);

		var braveRoot = Combine(local, "BraveSoftware", "Brave-Browser", "User Data");
		browserLocations = ProfileCacheLocations(braveRoot, browserCacheSuffixes)
			.Concat(BrowserTransientLocations(braveRoot))
			.ToArray();
		Add(targets, "brave-cache", CacheCategory,
			new("Brave 缓存", "Brave cache"),
			Regenerable, browserLocations);

		var vivaldiRoot = Combine(local, "Vivaldi", "User Data");
		browserLocations = ProfileCacheLocations(vivaldiRoot, browserCacheSuffixes)
			.Concat(BrowserTransientLocations(vivaldiRoot))
			.ToArray();
		Add(targets, "vivaldi-cache", CacheCategory,
			new("Vivaldi 缓存", "Vivaldi cache"),
			Regenerable, browserLocations);

		browserLocations = DirectCacheLocations(Combine(local, "Opera Software", "Opera Stable"),
			browserCacheSuffixes)
			.Concat(DirectCacheLocations(Combine(local, "Opera Software", "Opera GX Stable"),
				browserCacheSuffixes))
			.Concat(DirectCacheLocations(Combine(roaming, "Opera Software", "Opera Stable"),
				browserCacheSuffixes))
			.Concat(DirectCacheLocations(Combine(roaming, "Opera Software", "Opera GX Stable"),
				browserCacheSuffixes))
			.ToArray();
		Add(targets, "opera-cache", CacheCategory,
			new("Opera / Opera GX 缓存", "Opera / Opera GX cache"),
			Regenerable, browserLocations);

		var firefoxLocations = ProfileCacheLocations(Combine(local, "Mozilla", "Firefox", "Profiles"), "cache2", "startupCache");
		Add(targets, "firefox-cache", CacheCategory,
			new("Mozilla Firefox 缓存", "Mozilla Firefox cache"),
			Regenerable, firefoxLocations);

		var yandexLocations = ProfileCacheLocations(
			Combine(local, "Yandex", "YandexBrowser", "User Data"),
			browserCacheSuffixes)
			.ToArray();
		Add(targets, "yandex-cache", CacheCategory,
			new("Yandex 浏览器缓存", "Yandex Browser cache"),
			Regenerable, yandexLocations);

		var packagesRoot = Combine(local, "Packages");
		var applicationCaches = new List<CleanupLocation>();
		AddCacheLocations(applicationCaches, local, "Adobe", "Acrobat", "DC", "Cache");
		AddCacheLocations(applicationCaches, local, "Adobe", "AcroCef", "DC", "Acrobat", "Cache");
		AddCacheLocations(applicationCaches, local, "Adobe", "Acrobat", "DC", "WebCache");
		AddCacheLocations(applicationCaches, local, "Microsoft", "Windows", "Caches");
		AddCacheLocations(applicationCaches, local, "NVIDIA Corporation", "NVIDIA App", "CefCache");
		AddCacheLocations(applicationCaches, local, "NVIDIA Corporation", "NVIDIA App", "Cache");
		AddCacheLocations(applicationCaches, local, "NVIDIA Corporation", "NVIDIA Overlay", "CefCache");
		applicationCaches.AddRange(ProfileSubdirectoryLocations(
			Combine(local, "Microsoft", "VisualStudio"),
			"ComponentModelCache", "ImageLibrary", "WebView2Cache"));
		Add(targets, "application-caches", CacheCategory,
			new("常见应用程序缓存", "Common application caches"),
			new("只清理明确命名的应用缓存，不碰 Adobe 文档、OneDrive 同步/离线数据、Visual Studio 安装包缓存或应用配置。", "Only explicitly named application caches are considered; Adobe documents, OneDrive sync/offline data, Visual Studio installer packages, and app configuration are untouched."),
			applicationCaches);

		var packagedAppCaches = new List<CleanupLocation>();
		var packagedCacheSuffixes = new[]
		{
			"Cache",
			"Code Cache",
			"GPUCache",
			Path.Combine("Service Worker", "CacheStorage")
		};
		foreach (var packagePattern in new[]
		{
			"Microsoft.WindowsStore_*", "Microsoft.GamingApp_*", "Microsoft.XboxApp_*",
			"SpotifyAB.SpotifyMusic_*", "NVIDIACorp.NVIDIAControlPanel_*",
			"NVIDIACorp.NVIDIAApp_*", "AdvancedMicroDevicesInc-2.AMDRadeonSoftware_*",
			"AppUp.IntelGraphicsExperience_*", "AdobeAcrobatReader_*", "Microsoft.OneDriveSync_*"
		})
		{
			packagedAppCaches.AddRange(PackageCacheLocations(packagesRoot, packagePattern, "LocalCache", packagedCacheSuffixes));
		}
		Add(targets, "packaged-app-caches", CacheCategory,
			new("Microsoft Store/UWP 应用缓存", "Microsoft Store/UWP app caches"),
			new("只处理已知应用包 LocalCache 下明确命名的缓存目录；不碰 LocalState、设置、账号、同步数据或应用安装包。", "Only explicitly named cache directories under known app-package LocalCache folders are considered; LocalState, settings, accounts, sync data, and app packages are untouched."),
			packagedAppCaches, false, CleanupRisk.Moderate, optIn: true);

		var communicationCaches = new List<CleanupLocation>();
		AddCacheLocations(communicationCaches, local, "Microsoft", "Teams", "Cache", "Code Cache", "GPUCache", Path.Combine("Service Worker", "CacheStorage"));
		AddCacheLocations(communicationCaches, roaming, "Microsoft", "Teams", "Cache", "Code Cache", "GPUCache", Path.Combine("Service Worker", "CacheStorage"));
		AddCacheLocations(communicationCaches, local, "Discord", "Cache", "Code Cache", "GPUCache", "DawnCache");
		AddCacheLocations(communicationCaches, roaming, "discord", "Cache", "Code Cache", "GPUCache", "DawnCache");
		AddCacheLocations(communicationCaches, local, "Slack", "Cache", "Code Cache", "GPUCache", Path.Combine("Service Worker", "CacheStorage"));
		AddCacheLocations(communicationCaches, roaming, "Slack", "Cache", "Code Cache", "GPUCache", Path.Combine("Service Worker", "CacheStorage"));
		AddCacheLocations(communicationCaches, local, "Signal", "Cache", "Code Cache", "GPUCache");
		AddCacheLocations(communicationCaches, roaming, "Signal", "Cache", "Code Cache", "GPUCache");
		AddCacheLocations(communicationCaches, local, "Skype for Desktop", "Cache", "Code Cache", "GPUCache");
		AddCacheLocations(communicationCaches, roaming, "Skype for Desktop", "Cache", "Code Cache", "GPUCache");
		AddCacheLocations(communicationCaches, local, "Zoom", "Cache", "Code Cache", "GPUCache");
		AddCacheLocations(communicationCaches, roaming, "Zoom", "Cache", "Code Cache", "GPUCache");
		foreach (var application in new[] { "DingTalk", "钉钉", "Feishu", "飞书", "Lark", "WhatsApp" })
		{
			AddCacheLocations(communicationCaches, local, application, "Cache", "Code Cache", "GPUCache", "DawnCache");
			AddCacheLocations(communicationCaches, roaming, application, "Cache", "Code Cache", "GPUCache", "DawnCache");
			communicationCaches.AddRange(ProfileSubdirectoryLocations(
				Combine(local, application), "Cache", "Code Cache", "GPUCache", "DawnCache"));
			communicationCaches.AddRange(ProfileSubdirectoryLocations(
				Combine(roaming, application), "Cache", "Code Cache", "GPUCache", "DawnCache"));
		}
		AddCacheLocations(communicationCaches, local, "Tencent", "WeChat", "Cache", "Code Cache", "GPUCache");
		AddCacheLocations(communicationCaches, roaming, "Tencent", "WeChat", "Cache", "Code Cache", "GPUCache");
		AddCacheLocations(communicationCaches, local, "Tencent", "QQ", "Cache", "Code Cache", "GPUCache");
		AddCacheLocations(communicationCaches, roaming, "Tencent", "QQ", "Cache", "Code Cache", "GPUCache");
		communicationCaches.AddRange(ProfileSubdirectoryLocations(
			Combine(local, "Tencent", "WeChat"), "Cache", "Code Cache", "GPUCache"));
		communicationCaches.AddRange(ProfileSubdirectoryLocations(
			Combine(roaming, "Tencent", "WeChat"), "Cache", "Code Cache", "GPUCache"));
		communicationCaches.AddRange(ProfileSubdirectoryLocations(
			Combine(local, "Tencent", "QQ"), "Cache", "Code Cache", "GPUCache"));
		communicationCaches.AddRange(ProfileSubdirectoryLocations(
			Combine(roaming, "Tencent", "QQ"), "Cache", "Code Cache", "GPUCache"));
		var wechatFilesRoots = GetWeChatStorageRoots(documents);
		foreach (var wechatFilesRoot in wechatFilesRoots)
		{
			communicationCaches.AddRange(ProfileSubdirectoryLocations(
				wechatFilesRoot,
				Path.Combine("FileStorage", "Cache")));
			communicationCaches.AddRange(ProfileSubdirectoryLocations(
				wechatFilesRoot,
				Path.Combine("FileStorage", "Temp"))
				.Select(location => location with { MinimumAgeDays = 7 }));
			AddPathIfPresent(communicationCaches, Combine(wechatFilesRoot, "FileStorage", "Cache"));
			var directWeChatTemp = Combine(wechatFilesRoot, "FileStorage", "Temp");
			if (!string.IsNullOrWhiteSpace(directWeChatTemp))
			{
				communicationCaches.Add(new CleanupLocation(directWeChatTemp, MinimumAgeDays: 7));
			}
		}
		var tencentFilesRoot = Combine(documents, "Tencent Files");
		communicationCaches.AddRange(ProfileSubdirectoryLocations(tencentFilesRoot, "Cache"));
		communicationCaches.AddRange(ProfileSubdirectoryLocations(tencentFilesRoot, "Temp")
			.Select(location => location with { MinimumAgeDays = 7 }));
		communicationCaches.AddRange(ProfileNestedSubdirectoryLocations(
			tencentFilesRoot,
			Path.Combine("nt_qq", "nt_data"),
			"Cache"));
		communicationCaches.AddRange(ProfileNestedSubdirectoryLocations(
			tencentFilesRoot,
			Path.Combine("nt_qq", "nt_data"),
			"Temp")
			.Select(location => location with { MinimumAgeDays = 7 }));
		communicationCaches.AddRange(ProfileNestedSubdirectoryLocations(
			Combine(local, "Tencent", "QQ"),
			Path.Combine("nt_qq", "nt_data"),
			"Cache"));
		communicationCaches.AddRange(ProfileNestedSubdirectoryLocations(
			Combine(roaming, "Tencent", "QQ"),
			Path.Combine("nt_qq", "nt_data"),
			"Cache"));
		AddPathIfPresent(communicationCaches, Combine(local, "Tencent", "QQ", "nt_qq", "nt_data", "Cache"));
		AddPathIfPresent(communicationCaches, Combine(roaming, "Tencent", "QQ", "nt_qq", "nt_data", "Cache"));
		communicationCaches.AddRange(PackageCacheLocations(
			packagesRoot,
			"MSTeams_*",
			Path.Combine("LocalCache", "Microsoft", "MSTeams"),
			"Cache", "Code Cache", "GPUCache", Path.Combine("Service Worker", "CacheStorage")));
		communicationCaches.AddRange(PackageCacheLocations(
			packagesRoot,
			"MicrosoftTeams_*",
			Path.Combine("LocalCache", "Microsoft", "MSTeams"),
			"Cache", "Code Cache", "GPUCache", Path.Combine("Service Worker", "CacheStorage")));
		communicationCaches.AddRange(PackageCacheLocations(
			packagesRoot,
			"5319275A.WhatsAppDesktop_*",
			"LocalCache",
			"Cache", "Code Cache", "GPUCache", Path.Combine("Service Worker", "CacheStorage")));
		Add(targets, "communication-caches", CacheCategory,
			new("聊天/通讯软件缓存", "Chat and communication app caches"),
			new("只清理明确命名的缓存目录；微信/QQ 的传输 Temp 仅处理超过 7 天的文件。不碰聊天数据库、图片、视频、文件、附件、账号或会话文件；默认需手动选择。", "Only explicitly named cache directories are considered; WeChat/QQ transfer Temp files are limited to items older than 7 days. Chat databases, images, videos, files, attachments, accounts, and session data are untouched. Manual opt-in is required."),
			communicationCaches, false, CleanupRisk.Moderate, optIn: true);

		var communicationLogs = OldLocations(30,
			Combine(local, "Microsoft", "Teams", "logs"),
			Combine(roaming, "Microsoft", "Teams", "logs"),
			Combine(local, "Discord", "logs"),
			Combine(roaming, "discord", "logs"),
			Combine(local, "Slack", "logs"),
			Combine(roaming, "Slack", "logs"),
			Combine(local, "Signal", "logs"),
			Combine(roaming, "Signal", "logs"),
			Combine(local, "Skype for Desktop", "logs"),
			Combine(roaming, "Skype for Desktop", "logs"),
			Combine(local, "Zoom", "logs"),
			Combine(roaming, "Zoom", "logs"))
			.Concat(new[] { "DingTalk", "钉钉", "Feishu", "飞书", "Lark", "WhatsApp" }
				.SelectMany(application => OldLocations(30,
					Combine(local, application, "logs"),
					Combine(roaming, application, "logs"))))
		.Concat(wechatFilesRoots.SelectMany(root => ProfileSubdirectoryLocations(
			root,
			"Logs", "Log", Path.Combine("FileStorage", "Logs"), Path.Combine("FileStorage", "Log")))
				.Select(location => location with { MinimumAgeDays = 30 }))
			.Concat(ProfileSubdirectoryLocations(tencentFilesRoot, "Logs", "Log")
				.Select(location => location with { MinimumAgeDays = 30 }))
			.ToArray();
		Add(targets, "communication-logs", PrivacyCategory,
			new("聊天/通讯软件旧日志", "Old chat/communication logs"),
			new("只处理超过 30 天的日志；日志可能包含诊断信息，默认需手动选择。", "Only logs older than 30 days are considered; logs may contain diagnostic information. Manual opt-in is required."),
			communicationLogs, false, CleanupRisk.Moderate, optIn: true);

		var videoApplications = new[]
		{
			"Douyin", "抖音", "ByteDance", "bilibili", "哔哩哔哩", "Douyu", "斗鱼", "Huya", "虎牙",
			"Kwai", "快手", "YY", "TikTok", "Tencent Video", "腾讯视频", "QQLive", "Youku", "优酷",
			"iQIYI", "IQIYI", "爱奇艺", "MangoTV", "芒果TV", "XiguaVideo", "西瓜视频"
		};
		var videoCacheSuffixes = new[]
		{
			"Cache",
			"Code Cache",
			"GPUCache",
			"Media Cache",
			Path.Combine("Service Worker", "CacheStorage")
		};
		var videoCaches = new List<CleanupLocation>();
		foreach (var application in videoApplications)
		{
			var cachePaths = new[] { application }.Concat(videoCacheSuffixes).ToArray();
			AddCacheLocations(videoCaches, local, cachePaths);
			AddCacheLocations(videoCaches, roaming, cachePaths);
			videoCaches.AddRange(ProfileSubdirectoryLocations(Combine(local, application), videoCacheSuffixes));
			videoCaches.AddRange(ProfileSubdirectoryLocations(Combine(roaming, application), videoCacheSuffixes));
		}

		foreach (var packagePattern in new[]
		{
			"Douyin*", "ByteDance*", "BiliBili*", "Bilibili*", "Kwai*", "Huya*", "Douyu*",
			"TencentVideo*", "QQLive*", "Youku*", "iQIYI*", "IQIYI*", "MangoTV*", "Xigua*"
		})
		{
			videoCaches.AddRange(PackageCacheLocations(packagesRoot, packagePattern, "LocalCache", videoCacheSuffixes));
		}

		Add(targets, "video-live-caches", MediaCategory,
			new("抖音/B站/直播软件缓存", "Douyin/Bilibili/live-app caches"),
			new("只清理明确命名的 Cache、GPUCache、Code Cache、Media Cache；不碰下载视频、离线缓存、播放记录、账号和配置；默认需手动选择。", "Only explicitly named Cache, GPUCache, Code Cache, and Media Cache directories are considered; downloaded videos, offline media, watch history, accounts, and configuration are untouched. Manual opt-in is required."),
			videoCaches, false, CleanupRisk.Moderate, optIn: true);

		var videoLogs = new List<CleanupLocation>();
		foreach (var application in videoApplications)
		{
			videoLogs.AddRange(OldLocations(30,
				Combine(local, application, "Logs"),
				Combine(local, application, "logs"),
				Combine(roaming, application, "Logs"),
				Combine(roaming, application, "logs")));
			videoLogs.AddRange(ProfileSubdirectoryLocations(Combine(local, application), "Logs", "logs")
				.Select(location => location with { MinimumAgeDays = 30 }));
			videoLogs.AddRange(ProfileSubdirectoryLocations(Combine(roaming, application), "Logs", "logs")
				.Select(location => location with { MinimumAgeDays = 30 }));
		}
		videoLogs.AddRange(OldLocations(30,
			Combine(roaming, "obs-studio", "logs"),
			Combine(roaming, "obs-studio", "crashes"),
			Combine(local, "obs-studio", "logs"),
			Combine(local, "obs-studio", "crashes")));
		Add(targets, "video-live-logs", PrivacyCategory,
			new("视频/直播软件旧日志", "Old video/live-app logs"),
			new("只处理超过 30 天的日志和崩溃报告；不碰 OBS 场景、插件、录制文件或软件配置，默认需手动选择。", "Only logs and crash reports older than 30 days are considered; OBS scenes, plugins, recordings, and app configuration are untouched. Manual opt-in is required."),
			videoLogs, false, CleanupRisk.Moderate, optIn: true);

		var launcherCaches = new List<CleanupLocation>();
		AddCacheLocations(launcherCaches, programFilesX86, "Steam", "htmlcache");
		AddCacheLocations(launcherCaches, programFilesX86, "Steam", "shadercache");
		AddCacheLocations(launcherCaches, programFiles, "Steam", "htmlcache");
		AddCacheLocations(launcherCaches, programFiles, "Steam", "shadercache");
		AddCacheLocations(launcherCaches, local, "EpicGamesLauncher", "Saved", "webcache");
		AddCacheLocations(launcherCaches, local, "EpicGamesLauncher", "Saved", "webcache_4147");
		AddCacheLocations(launcherCaches, local, "EpicGamesLauncher", "Saved", "webcache_4430");
		AddCacheLocations(launcherCaches, local, "Battle.net", "Cache");
		AddCacheLocations(launcherCaches, local, "Blizzard Entertainment", "Battle.net", "Cache");
		AddCacheLocations(launcherCaches, local, "GOG.com", "Galaxy", "webcache");
		AddCacheLocations(launcherCaches, local, "Electronic Arts", "EA Desktop", "cache");
		AddCacheLocations(launcherCaches, local, "Riot Games", "Riot Client", "UX", "Cache", "Code Cache", "GPUCache");
		AddCacheLocations(launcherCaches, local, "Ubisoft Game Launcher", "cache", "webcache");
		AddCacheLocations(launcherCaches, roaming, "Ubisoft Game Launcher", "cache", "webcache");
		AddCacheLocations(launcherCaches, local, "Rockstar Games", "Launcher", "webcache", "Cache");
		AddCacheLocations(launcherCaches, roaming, "Rockstar Games", "Launcher", "webcache", "Cache");
		foreach (var steamLibrary in SteamLibraryDiscovery.FindLibraries())
		{
			AddPathIfPresent(launcherCaches, Combine(steamLibrary, "htmlcache"));
			AddPathIfPresent(launcherCaches, Combine(steamLibrary, "appcache", "httpcache"));
			AddPathIfPresent(launcherCaches, Combine(steamLibrary, "steamapps", "shadercache"));
		}
		Add(targets, "launcher-caches", CacheCategory,
			new("游戏启动器网页/着色器缓存", "Game launcher web/shader caches"),
			new("只清理启动器明确标记为缓存的目录，不碰游戏文件、存档或登录信息；默认需手动选择。", "Only launcher directories explicitly named as caches are considered; game files, saves, and login data are untouched. Manual opt-in is required."),
			launcherCaches, false, CleanupRisk.Moderate, optIn: true);

		var launcherLogs = OldLocations(30,
			Combine(local, "EpicGamesLauncher", "Saved", "Logs"),
			Combine(local, "Battle.net", "Logs"),
			Combine(local, "Blizzard Entertainment", "Battle.net", "Logs"),
			Combine(local, "GOG.com", "Galaxy", "logs"),
			Combine(local, "Electronic Arts", "EA Desktop", "logs"),
			Combine(local, "Riot Games", "Riot Client", "Logs"),
			Combine(local, "Ubisoft Game Launcher", "logs"),
			Combine(roaming, "Ubisoft Game Launcher", "logs"),
			Combine(local, "Rockstar Games", "Launcher", "Logs"),
			Combine(roaming, "Rockstar Games", "Launcher", "Logs"));
		Add(targets, "launcher-logs", PrivacyCategory,
			new("游戏启动器旧日志", "Old game launcher logs"),
			new("只处理超过 30 天的启动器日志，不碰游戏文件、存档、下载队列或登录数据；默认需手动选择。", "Only launcher logs older than 30 days are considered; game files, saves, download queues, and login data are untouched. Manual opt-in is required."),
			launcherLogs, false, CleanupRisk.Moderate, optIn: true);

		var updaterTransientFiles = new List<CleanupLocation>();
		foreach (var updaterPath in new[]
		{
			Combine(local, "Google", "Update", "Download"),
			Combine(local, "Microsoft", "EdgeUpdate", "Download"),
			Combine(programData, "Microsoft", "EdgeUpdate", "Download"),
			Combine(local, "SquirrelTemp"),
			Combine(roaming, "Microsoft", "Teams", "packages", "squirreltemp")
		})
		{
			updaterTransientFiles.AddRange(OldMatchingLocations(14, updaterPath, true,
				"*.tmp", "*.temp", "*.crdownload", "*.partial", "*.download", "*.log", "*.etl"));
		}
		Add(targets, "old-updater-transients", CacheCategory,
			new("旧软件升级临时碎片/日志", "Old software updater fragments/logs"),
			new("只处理常见软件自有更新目录中超过 14 天的临时碎片和日志；完整安装包/升级包需使用单独的逐文件审核，不碰用户 Downloads。", "Only temporary fragments and logs older than 14 days in common app-owned updater directories are considered; complete installer/update packages require separate per-file review, and user Downloads is untouched."),
			updaterTransientFiles, false, CleanupRisk.Moderate, optIn: true);

		var aiCaches = new List<CleanupLocation>();
		AddPathIfPresent(aiCaches, Combine(userProfile, ".codex", "cache"));
		AddPathIfPresent(aiCaches, Combine(userProfile, ".codex", "tmp"));
		AddCacheLocations(aiCaches, userProfile, ".gemini", "cache");
		AddCacheLocations(aiCaches, local, "Claude", "Cache", "Code Cache", "GPUCache");
		AddCacheLocations(aiCaches, roaming, "Claude", "Cache", "Code Cache", "GPUCache");
		AddCacheLocations(aiCaches, local, "OpenAI", "Cache", "Code Cache", "GPUCache");
		AddCacheLocations(aiCaches, roaming, "OpenAI", "Cache", "Code Cache", "GPUCache");
		AddCacheLocations(aiCaches, local, "ChatGPT", "Cache", "Code Cache", "GPUCache");
		AddCacheLocations(aiCaches, roaming, "ChatGPT", "Cache", "Code Cache", "GPUCache");
		AddCacheLocations(aiCaches, local, "Cursor", "Cache", "CachedData", "Code Cache", "GPUCache");
		AddCacheLocations(aiCaches, roaming, "Cursor", "Cache", "CachedData", "Code Cache", "GPUCache");
		AddCacheLocations(aiCaches, local, "Windsurf", "Cache", "CachedData", "Code Cache", "GPUCache");
		AddCacheLocations(aiCaches, roaming, "Windsurf", "Cache", "CachedData", "Code Cache", "GPUCache");
		aiCaches.AddRange(PackageCacheLocations(packagesRoot, "OpenAI*", "LocalCache", "Cache", "Code Cache", "GPUCache"));
		aiCaches.AddRange(PackageCacheLocations(packagesRoot, "ChatGPT*", "LocalCache", "Cache", "Code Cache", "GPUCache"));
		aiCaches.AddRange(PackageCacheLocations(packagesRoot, "Claude*", "LocalCache", "Cache", "Code Cache", "GPUCache"));
		Add(targets, "ai-tool-caches", AiCategory,
			new("AI 工具缓存（含 .codex）", "AI tool caches (including .codex)"),
			new("只清理明确的缓存/临时目录；不碰 .codex 的会话、项目、技能、插件、附件、记忆、配置或密钥，也不碰 AI 模型文件；默认需手动选择。", "Only explicitly identified cache/temp directories are considered; .codex sessions, projects, skills, plugins, attachments, memories, config, and credentials—and AI model files—are untouched. Manual opt-in is required."),
			aiCaches, false, CleanupRisk.Moderate, optIn: true);

		var aiLogs = OldLocations(30,
			Combine(userProfile, ".codex", "logs"),
			Combine(userProfile, ".gemini", "logs"),
			Combine(local, "Claude", "logs"),
			Combine(roaming, "Claude", "logs"),
			Combine(local, "OpenAI", "logs"),
			Combine(roaming, "OpenAI", "logs"),
			Combine(local, "ChatGPT", "logs"),
			Combine(roaming, "ChatGPT", "logs"),
			Combine(local, "Cursor", "logs"),
			Combine(roaming, "Cursor", "logs"),
			Combine(local, "Windsurf", "logs"),
			Combine(roaming, "Windsurf", "logs"));
		Add(targets, "ai-tool-logs", PrivacyCategory,
			new("AI 工具旧日志", "Old AI tool logs"),
			new("只处理超过 30 天的日志；日志可能包含提示词或诊断信息，默认需手动选择。", "Only logs older than 30 days are considered; logs may contain prompts or diagnostics. Manual opt-in is required."),
			aiLogs, false, CleanupRisk.Moderate, optIn: true);

		var developerCaches = new List<CleanupLocation>();
		AddCacheLocations(developerCaches, local, "npm-cache");
		AddPathIfPresent(developerCaches, GetEnvironmentPath("npm_config_cache"));
		AddCacheLocations(developerCaches, local, "pip", "Cache");
		AddPathIfPresent(developerCaches, GetEnvironmentPath("PIP_CACHE_DIR"));
		AddCacheLocations(developerCaches, local, "NuGet", "v3-cache");
		AddCacheLocations(developerCaches, local, "NuGet", "plugins-cache");
		AddPathIfPresent(developerCaches, GetEnvironmentPath("NUGET_HTTP_CACHE_PATH"));
		AddCacheLocations(developerCaches, local, "Yarn", "Cache");
		AddPathIfPresent(developerCaches, GetEnvironmentPath("YARN_CACHE_FOLDER"));
		AddPathIfPresent(developerCaches, Combine(local, "pnpm", "store"));
		AddPathIfPresent(developerCaches, Combine(userProfile, ".pnpm-store"));
		AddPathIfPresent(developerCaches, Combine(programData, "chocolatey", "cache"));
		AddPathIfPresent(developerCaches, Combine(userProfile, "scoop", "cache"));
		AddCacheLocations(developerCaches, roaming, "Code", "Cache");
		AddCacheLocations(developerCaches, roaming, "Code", "CachedData");
		AddCacheLocations(developerCaches, roaming, "Code", "Code Cache");
		AddCacheLocations(developerCaches, roaming, "Code", "GPUCache");
		AddCacheLocations(developerCaches, local, "Code", "Cache");
		AddCacheLocations(developerCaches, local, "Code", "CachedData");
		AddCacheLocations(developerCaches, local, "Code", "Code Cache");
		AddCacheLocations(developerCaches, local, "Code", "GPUCache");
		AddCacheLocations(developerCaches, roaming, "Code - Insiders", "Cache", "CachedData", "Code Cache", "GPUCache");
		AddCacheLocations(developerCaches, local, "Code - Insiders", "Cache", "CachedData", "Code Cache", "GPUCache");
		AddPathIfPresent(developerCaches, Combine(userProfile, ".gradle", "caches"));
		developerCaches.AddRange(ProfileSubdirectoryLocations(Combine(local, "JetBrains"), "caches"));
		developerCaches.AddRange(ProfileSubdirectoryLocations(Combine(local, "JetBrains"), "log", "tmp")
			.Select(location => location with { MinimumAgeDays = 30 }));
		AddPathIfPresent(developerCaches, Combine(userProfile, ".android", "cache"));

		var ageLimitedDeveloperCaches = new List<CleanupLocation>();
		ageLimitedDeveloperCaches.AddRange(OldLocations(90,
			Combine(userProfile, ".m2", "repository")));
		ageLimitedDeveloperCaches.AddRange(OldLocations(30,
			Combine(local, "go-build"),
			GetEnvironmentPath("GOCACHE")));
		foreach (var gopath in GetEnvironmentPaths("GOPATH").Append(Combine(userProfile, "go")).Where(path => !string.IsNullOrWhiteSpace(path)))
		{
			ageLimitedDeveloperCaches.AddRange(OldLocations(30,
				Combine(gopath, "pkg", "mod", "cache", "download")));
		}

		foreach (var cargoHome in GetEnvironmentPaths("CARGO_HOME").Append(Combine(userProfile, ".cargo")).Where(path => !string.IsNullOrWhiteSpace(path)))
		{
			ageLimitedDeveloperCaches.AddRange(OldLocations(30,
				Combine(cargoHome, "registry", "cache"),
				Combine(cargoHome, "registry", "src"),
				Combine(cargoHome, "git", "db"),
				Combine(cargoHome, "git", "checkouts")));
		}

		foreach (var condaPackages in GetEnvironmentPaths("CONDA_PKGS_DIRS"))
		{
			ageLimitedDeveloperCaches.AddRange(OldMatchingLocations(30, condaPackages, true, "*.conda", "*.tar.bz2"));
		}

		developerCaches.AddRange(ageLimitedDeveloperCaches);
		Add(targets, "developer-caches", DeveloperCategory,
			new("开发工具下载/构建缓存", "Developer download/build caches"),
			new("包含 npm/pip/NuGet/Yarn/pnpm、Gradle、JetBrains、Maven、Cargo、Go、Conda、Scoop/Chocolatey 等明确缓存或依赖目录；清理后可能需要重新下载，默认不纳入 --all。", "Includes explicit npm/pip/NuGet/Yarn/pnpm, Gradle, JetBrains, Maven, Cargo, Go, Conda, Scoop/Chocolatey cache or dependency directories; cleanup may require downloads and is excluded from --all by default."),
			developerCaches, false, CleanupRisk.Moderate, optIn: true);

		Add(targets, "windows-old-logs", DiskCategory,
			new("旧 CBS / DISM 日志", "Old CBS / DISM logs"),
			new("只清理超过 30 天的诊断日志，不处理当前日志。", "Only diagnostic logs older than 30 days are removed; current logs are untouched."),
			OldLocations(30,
				Combine(windows, "Logs", "CBS"),
				Combine(windows, "Logs", "DISM")),
			true, CleanupRisk.Moderate, optIn: true);

		Add(targets, "prefetch-cache", DiskCategory,
			new("Windows Prefetch 缓存", "Windows Prefetch cache"),
			new("Windows 会自动重建，但首次启动应用可能需要重新生成预取数据；默认不纳入 --all。", "Windows recreates it, but first launches may rebuild prefetch data; excluded from --all by default."),
			Locations(Combine(windows, "Prefetch")), true, CleanupRisk.Moderate, optIn: true);

		return targets;
	}

	private static void Add(
		ICollection<CleanupTarget> targets,
		string id,
		LocalizedText category,
		LocalizedText name,
		LocalizedText note,
		IEnumerable<CleanupLocation> locations,
		bool requiresAdministrator = false,
		CleanupRisk risk = CleanupRisk.Safe,
		bool optIn = false)
	{
		targets.Add(new CleanupTarget(id, category, name, note, locations, requiresAdministrator, risk, optIn));
	}

	private static IEnumerable<CleanupLocation> Locations(params string?[] paths)
		=> paths
			.Where(path => !string.IsNullOrWhiteSpace(path) && !IsUserDownloadsPath(path))
			.Select(path => new CleanupLocation(path!));

	private static IEnumerable<CleanupLocation> Locations(
		string? path,
		CleanupLocationKind kind,
		string? pattern = null,
		bool recursive = true)
		=> string.IsNullOrWhiteSpace(path) || IsUserDownloadsPath(path)
			? Array.Empty<CleanupLocation>()
			: new[] { new CleanupLocation(path, kind, pattern, recursive) };

	private static IEnumerable<CleanupLocation> OldLocations(int minimumAgeDays, params string?[] paths)
		=> paths
			.Where(path => !string.IsNullOrWhiteSpace(path) && !IsUserDownloadsPath(path))
			.Select(path => new CleanupLocation(path!, MinimumAgeDays: minimumAgeDays));

	private static IEnumerable<CleanupLocation> OldMatchingLocations(
		int minimumAgeDays,
		string? path,
		bool recursive,
		params string[] patterns)
		=> string.IsNullOrWhiteSpace(path) || IsUserDownloadsPath(path)
			? Array.Empty<CleanupLocation>()
			: patterns
				.Where(pattern => !string.IsNullOrWhiteSpace(pattern))
				.Select(pattern => new CleanupLocation(
					path!,
					CleanupLocationKind.MatchingFiles,
					pattern,
					recursive,
					minimumAgeDays))
			.ToArray();

	private static void AddCacheLocations(
		ICollection<CleanupLocation> locations,
		string root,
		params string[] pathParts)
	{
		var firstCachePart = Array.FindIndex(pathParts, part => part.Contains("Cache", StringComparison.OrdinalIgnoreCase));
		if (firstCachePart < 0)
		{
			return;
		}

		var appParts = pathParts.Take(firstCachePart).ToArray();
		foreach (var cachePart in pathParts.Skip(firstCachePart))
		{
			var path = Combine(root, appParts.Concat(new[] { cachePart }).ToArray());
			if (!string.IsNullOrWhiteSpace(path) && !IsUserDownloadsPath(path))
			{
				locations.Add(new CleanupLocation(path));
			}
		}
	}

	private static IEnumerable<CleanupLocation> ProfileCacheLocations(string? profileRoot, params string[] suffixes)
	{
		if (string.IsNullOrWhiteSpace(profileRoot) || IsUserDownloadsPath(profileRoot) || !Directory.Exists(profileRoot))
		{
			return Array.Empty<CleanupLocation>();
		}

		try
		{
			return Directory.EnumerateDirectories(profileRoot, "*", SearchOption.TopDirectoryOnly)
				.Where(path => !IsReparsePoint(path))
				.SelectMany(profile => suffixes.Select(suffix => new CleanupLocation(Path.Combine(profile, suffix))))
				.ToArray();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return Array.Empty<CleanupLocation>();
		}
	}

	private static IEnumerable<CleanupLocation> DirectCacheLocations(string? root, params string[] suffixes)
	{
		if (string.IsNullOrWhiteSpace(root) || IsUserDownloadsPath(root))
		{
			return Array.Empty<CleanupLocation>();
		}

		return suffixes.Select(suffix => new CleanupLocation(Path.Combine(root, suffix))).ToArray();
	}

	private static IEnumerable<CleanupLocation> BrowserTransientLocations(string? profileRoot)
	{
		if (string.IsNullOrWhiteSpace(profileRoot) || IsUserDownloadsPath(profileRoot))
		{
			return Array.Empty<CleanupLocation>();
		}

		return new[]
		{
			new CleanupLocation(profileRoot, CleanupLocationKind.MatchingFiles, "B*.tmp", false)
		};
	}

	private static IEnumerable<CleanupLocation> ProfileSubdirectoryLocations(string? root, params string[] suffixes)
	{
		if (string.IsNullOrWhiteSpace(root) || IsUserDownloadsPath(root) || !Directory.Exists(root))
		{
			return Array.Empty<CleanupLocation>();
		}

		try
		{
			return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
				.Where(path => !IsReparsePoint(path))
				.SelectMany(profile => suffixes.Select(suffix => new CleanupLocation(Path.Combine(profile, suffix))))
				.ToArray();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return Array.Empty<CleanupLocation>();
		}
	}

	private static IEnumerable<CleanupLocation> PackageCacheLocations(
		string? packagesRoot,
		string pattern,
		string relativeRoot,
		params string[] suffixes)
	{
		if (string.IsNullOrWhiteSpace(packagesRoot) || IsUserDownloadsPath(packagesRoot) || !Directory.Exists(packagesRoot))
		{
			return Array.Empty<CleanupLocation>();
		}

		try
		{
			return Directory.EnumerateDirectories(packagesRoot, pattern, SearchOption.TopDirectoryOnly)
				.Where(path => !IsReparsePoint(path))
				.SelectMany(package => suffixes.Select(suffix => new CleanupLocation(Path.Combine(package, relativeRoot, suffix))))
				.ToArray();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return Array.Empty<CleanupLocation>();
		}
	}

	private static IEnumerable<CleanupLocation> ProfileNestedSubdirectoryLocations(
		string? root,
		string relativeRoot,
		params string[] suffixes)
	{
		if (string.IsNullOrWhiteSpace(root) || IsUserDownloadsPath(root) || !Directory.Exists(root))
		{
			return Array.Empty<CleanupLocation>();
		}

		try
		{
			return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
				.Where(path => !IsReparsePoint(path))
				.SelectMany(profile => suffixes.Select(suffix => new CleanupLocation(Path.Combine(profile, relativeRoot, suffix))))
				.ToArray();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return Array.Empty<CleanupLocation>();
		}
	}

	private static void AddPathIfPresent(ICollection<CleanupLocation> locations, string? path)
	{
		var expandedPath = NormalizeOptionalPath(path);
		if (!string.IsNullOrWhiteSpace(expandedPath))
		{
			locations.Add(new CleanupLocation(expandedPath));
		}
	}

	private static bool IsUserDownloadsPath(string? path)
		=> UserFolderDiscovery.IsSameOrDescendant(path, UserFolderDiscovery.GetDownloadsPath());

	private static string? GetEnvironmentPath(string variableName)
		=> NormalizeOptionalPath(Environment.GetEnvironmentVariable(variableName));

	private static IEnumerable<string> GetEnvironmentPaths(string variableName)
	{
		var value = Environment.GetEnvironmentVariable(variableName);
		if (string.IsNullOrWhiteSpace(value))
		{
			return Array.Empty<string>();
		}

		return value
			.Split(new[] { ';', Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(NormalizeOptionalPath)
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(path => path!)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static string? ExpandEnvironmentPath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}

		try
		{
			return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			return null;
		}
	}

	private static string? NormalizeOptionalPath(string? path)
	{
		var expandedPath = ExpandEnvironmentPath(path);
		if (string.IsNullOrWhiteSpace(expandedPath))
		{
			return null;
		}

		try
		{
			if (!Path.IsPathRooted(expandedPath))
			{
				return null;
			}

			var fullPath = Path.GetFullPath(expandedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var pathRoot = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			var userDownloads = UserFolderDiscovery.GetDownloadsPath();
			var broadRoots = new[]
			{
				userProfile,
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
				Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
				Environment.GetFolderPath(Environment.SpecialFolder.Windows),
				Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
			};
			if (broadRoots.Any(root => !string.IsNullOrWhiteSpace(root) &&
				string.Equals(fullPath, root!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) ||
				UserFolderDiscovery.IsSameOrDescendant(fullPath, userDownloads))
			{
				return null;
			}

			return fullPath;
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return null;
		}
	}

	private static IReadOnlyList<string> GetWeChatStorageRoots(string? documents)
	{
		var roots = new List<string>();
		AddDistinctPath(roots, Combine(documents, "WeChat Files"));

		if (!OperatingSystem.IsWindows())
		{
			return roots;
		}

		try
		{
			using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Tencent\WeChat", writable: false);
			var configuredPath = key?.GetValue("FileSavePath")?.ToString()?.Trim().Trim('"');
			if (!string.IsNullOrWhiteSpace(configuredPath) &&
				!configuredPath.Equals("MyDocument:", StringComparison.OrdinalIgnoreCase))
			{
				AddDistinctPath(roots, Environment.ExpandEnvironmentVariables(configuredPath));
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
		{
			// The default Documents\WeChat Files location remains available if the
			// optional WeChat setting cannot be read.
		}

		return roots;
	}

	private static void AddDistinctPath(ICollection<string> paths, string? path)
	{
		if (string.IsNullOrWhiteSpace(path) ||
			UserFolderDiscovery.IsSameOrDescendant(path, UserFolderDiscovery.GetDownloadsPath()))
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

	private static bool IsReparsePoint(string path)
	{
		try
		{
			return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return true;
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
		catch (ArgumentException)
		{
			return null;
		}
	}

	private static string? GetSystemDriveRoot(string? windowsPath)
	{
		try
		{
			var path = windowsPath;
			if (string.IsNullOrWhiteSpace(path) && OperatingSystem.IsWindows())
			{
				path = Environment.SystemDirectory;
			}

			return string.IsNullOrWhiteSpace(path) ? null : Path.GetPathRoot(Path.GetFullPath(path));
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return null;
		}
	}
}
