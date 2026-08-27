using System.Globalization;

namespace WindowsCleanup.Localization;

public enum AppLanguage
{
	Chinese,
	English
}

public sealed record LocalizedText(string Chinese, string English)
{
	public string Get(AppLanguage language) => language == AppLanguage.Chinese ? Chinese : English;
}

public sealed class Localizer
{
	private static readonly IReadOnlyDictionary<string, LocalizedText> Values =
		new Dictionary<string, LocalizedText>(StringComparer.Ordinal)
		{
			["app_title"] = new("Windows 安全清理工具", "Windows Safe Cleanup"),
			["welcome_header"] = new("安全清理 Windows 10/11", "Safe cleanup for Windows 10/11"),
			["welcome_body"] = new(
				"此工具默认只处理可重新生成的临时文件、缓存和当前用户的使用记录；安装包、升级包和卸载残留采用人工审核。\n" +
				"删除前会显示扫描结果并要求确认。",
				"By default this tool handles regenerable temporary files, caches, and current-user usage records; installers, updates, and uninstall remnants require manual review.\n" +
				"A scan is shown and confirmation is required before deletion."),
			["welcome_safety"] = new(
				"安全边界：不删除 WinSxS、Windows\\Installer、Package Cache、Visual Studio 包缓存、驱动、服务配置或系统关键注册表；有风险的项目默认不纳入 --all，用户个人文件夹也不会被默认操作。",
				"Safety boundary: WinSxS, Windows\\Installer, Package Cache, Visual Studio package cache, drivers, services, and system-critical registry data are never deleted; review-only items are excluded from --all, and user-owned folders are not touched by default."),
			["menu_title"] = new("请选择操作", "Choose an action"),
			["menu_clean"] = new("清理 C 盘安全项目（临时文件和缓存）", "Clean safe C: items (temporary files and caches)"),
			["menu_registry"] = new("清理当前用户注册表使用记录", "Clean current-user registry history"),
			["menu_duplicates"] = new("查找重复文件（仅列表展示）", "List duplicate files (no deletion)"),
			["menu_remnants"] = new("审核并清理卸载应用残留（可多选）", "Review and clean uninstalled-app residue (multi-select)"),
			["menu_packages"] = new("审核旧安装包/升级包（逐项确认）", "Review old installers/updates (confirm per item)"),
			["menu_migration"] = new("C 盘数据迁移（用户目录、开发包和支持的软件）", "Migrate C: data (user folders, developer caches, supported apps)"),
			["menu_exit"] = new("退出", "Exit"),
			["not_windows"] = new("此工具只支持 Windows 10/11。", "This tool supports Windows 10/11 only."),
			["scanning"] = new("正在扫描安全清理项目...", "Scanning safe cleanup items..."),
			["scan_complete"] = new("扫描完成。", "Scan complete."),
			["cleanup_plan"] = new("可清理项目", "Cleanup items found"),
			["cleanup_empty"] = new("没有发现可清理的安全项目。", "No safe cleanup items were found."),
			["cleanup_columns_category"] = new("类别", "Category"),
			["cleanup_columns_item"] = new("项目", "Item"),
			["cleanup_columns_risk"] = new("风险级别", "Risk"),
			["cleanup_columns_files"] = new("文件数", "Files"),
			["cleanup_columns_size"] = new("大小", "Size"),
			["cleanup_columns_note"] = new("说明", "Note"),
			["cleanup_note_admin"] = new("可能需要管理员权限", "May require administrator"),
			["cleanup_note_opt_in"] = new("需手动选择，不包含在 --all", "manual opt-in; excluded from --all"),
			["cleanup_risk_safe"] = new("安全", "Safe"),
			["cleanup_risk_moderate"] = new("需确认", "Review"),
			["cleanup_note_safe"] = new("可重新生成", "Regenerable"),
			["cleanup_select"] = new("选择要清理的项目", "Select items to clean"),
			["cleanup_select_instructions"] = new("使用空格选择，回车确认。", "Press space to select, then enter to continue."),
			["cleanup_none_selected"] = new("没有选择项目，已取消。", "No items selected; cancelled."),
			["cleanup_all_skipped_opt_in"] = new("--all 已跳过需要额外判断或可能重新下载的项目；如需处理，请使用交互式 --clean 手动选择。", "--all skipped items requiring extra review or possible re-downloads; use interactive --clean to select them manually."),
			["cleanup_moderate_warning"] = new("所选项目属于需确认级别：内容可重建，但可能导致重新下载、首次启动变慢或无法从回收站恢复。", "The selection includes review-level items: they are rebuildable, but may require downloads, slow a first launch, or cannot be restored from the recycle bin."),
			["cleanup_confirm"] = new("确定删除选中的 {0} 个项目（约 {1}）吗？", "Delete the selected {0} item(s) (about {1})?"),
			["cleanup_cancelled"] = new("已取消，未删除任何文件。", "Cancelled; no files were deleted."),
			["cleaning_item"] = new("正在清理：{0}", "Cleaning: {0}"),
			["cleanup_result"] = new("清理结果：删除 {0} 个文件，释放约 {1}。", "Cleanup result: deleted {0} file(s), freeing about {1}."),
			["cleanup_failed"] = new("有 {0} 个文件无法删除，通常是因为正在使用或权限不足。", "{0} file(s) could not be deleted, usually because they are in use or permission is missing."),
			["cleanup_errors"] = new("部分错误：", "Some errors:"),
			["dry_run"] = new("预览模式：不会删除文件。", "Preview mode: no files will be deleted."),
			["not_admin"] = new("当前不是管理员；系统目录中被锁定或受保护的文件会自动跳过。", "The process is not elevated; locked or protected system files will be skipped."),
			["admin_available"] = new("当前以管理员运行。", "Running with administrator privileges."),
			["registry_title"] = new("当前用户注册表使用记录", "Current-user registry history"),
			["registry_warning"] = new(
				"只会修改 HKCU 中的最近打开、运行、搜索、路径、应用启动、兼容性助手、压缩工具历史和可重建的应用显示名称缓存。HKCU/HKLM 的卸载项只做路径失效候选审核，不会自动删除注册表项。",
				"Only HKCU recent-open, run, search, path, app-launch, Compatibility Assistant, archive-tool history, and regenerable display-name cache can be modified. HKCU/HKLM uninstall entries are checked for missing paths only and are never deleted automatically."),
			["registry_columns_path"] = new("注册表路径", "Registry path"),
			["registry_columns_entries"] = new("记录数", "Entries"),
			["registry_confirm"] = new("确定清理这些当前用户使用记录吗？", "Clear these current-user usage records?"),
			["registry_empty"] = new("没有找到可清理的注册表使用记录。", "No removable registry history was found."),
			["registry_orphan_header"] = new("卸载项路径失效候选（仅审核）", "Uninstall entries with missing paths (review only)"),
			["registry_orphan_columns_name"] = new("应用名称", "Application"),
			["registry_orphan_columns_location"] = new("注册表中的安装路径", "Registered install path"),
			["registry_orphan_columns_hive"] = new("位置", "Hive/view"),
			["registry_orphan_manual"] = new("路径不存在不等于应用一定已卸载（也可能是外置盘未连接）；请在注册表编辑器或卸载工具中逐项确认。工具不会删除这些项。", "A missing path does not prove that an app is uninstalled (the drive may be offline); verify each item with Windows or an uninstaller. This tool never deletes these entries."),
			["registry_history_empty_orphan_only"] = new("没有找到可清理的历史记录；上面的卸载项候选仅供人工审核。", "No registry history is available to clear; the uninstall candidates above are review-only."),
			["registry_done"] = new("注册表使用记录清理完成。", "Registry history cleanup complete."),
			["registry_failed"] = new("有 {0} 个注册表项目未能清理。", "{0} registry item(s) could not be cleared."),
			["registry_dry_run"] = new("预览模式：不会修改注册表。", "Preview mode: the registry will not be changed."),
			["duplicates_title"] = new("查找完全相同的重复文件", "Find byte-identical duplicate files"),
			["duplicates_roots"] = new("扫描目录（仅限桌面、文档、图片和视频）：", "Directories scanned (Desktop, Documents, Pictures, and Videos only):"),
			["duplicates_scanning"] = new("正在计算文件指纹，较大的目录可能需要一些时间...", "Calculating file fingerprints; large folders may take a while..."),
			["duplicates_none"] = new("没有找到完全相同的重复文件。", "No byte-identical duplicate files were found."),
			["duplicates_found"] = new("找到 {0} 组完全相同的重复文件；以下仅展示，不会删除：", "Found {0} byte-identical duplicate group(s); the list below is display-only and nothing will be deleted:"),
			["duplicates_group"] = new("第 {0} 组：{1}，共 {2} 份", "Group {0}: {1}, {2} copies"),
			["duplicates_list_only"] = new("重复文件仅供列表展示；本功能不会选择、删除或移动任何文件。", "Duplicate files are display-only; this operation does not select, delete, or move any file."),
			["duplicates_limit"] = new("只显示前 {0} 组；可缩小扫描目录后再次运行。", "Only the first {0} groups are shown; scan narrower folders to see more."),
			["remnants_title"] = new("卸载应用残留候选", "Uninstalled-app residue candidates"),
			["remnants_note"] = new(
				"候选来自当前用户 Local、Roaming 和 LocalLow 下的一级目录。工具排除 Windows/系统目录、常见仍在使用的软件、已安装软件注册表关联路径、包含可执行文件的目录，并只列出长期未变化的目录。表格中的路径在支持终端超链接的情况下可点击打开；删除时会再次核验，默认只删除目录内达到保守时间条件的文件。",
				"Candidates come from first-level folders under the current user's Local, Roaming, and LocalLow application-data roots. System/common folders, installed applications, registry-linked install paths, and directories containing executables are excluded. Paths in the table are clickable in terminals that support hyperlinks. Candidates are checked again before deletion; only sufficiently old files inside a selected directory are removed."),
			["remnants_none"] = new("没有找到符合保守规则的残留候选。", "No candidates matched the conservative rules."),
			["remnants_columns_path"] = new("路径", "Path"),
			["remnants_columns_date"] = new("最后变化", "Last change"),
			["remnants_columns_size"] = new("大小", "Size"),
			["remnants_manual"] = new("请确认软件已经卸载且不需要这些数据；可使用空格多选，删除前还会再次检查。", "Confirm that the applications are uninstalled and the data is no longer needed. Use space to multi-select; every candidate is checked again before deletion."),
			["remnants_select"] = new("选择要删除的残留候选（可多选）", "Select residue candidates to delete (multi-select)"),
			["remnants_confirm"] = new("确定删除选中的 {0} 个残留目录中的旧文件（约 {1}）吗？", "Delete old files inside the selected {0} residue directory/directories (about {1})?"),
			["remnants_deleted"] = new("残留清理完成：删除 {0} 个文件，释放约 {1}。", "Residue cleanup complete: deleted {0} file(s), freeing about {1}."),
			["remnants_skipped"] = new("有 {0} 个候选在确认时已不符合安全条件，因此跳过。", "{0} candidate(s) no longer matched the safety conditions at confirmation time and were skipped."),
			["packages_title"] = new("旧安装包和升级包候选", "Old installer and update package candidates"),
			["packages_note"] = new(
				"根据已安装软件卸载注册表中的 InstallLocation/DisplayIcon，以及明确的厂商/应用更新暂存目录建立候选范围；再按安装包扩展名、安装/升级/驱动等命名标记和目录语义筛选。只列出至少 20 MB 的候选，不进行全盘按文件名猜测；每个文件仍需人工勾选并再次确认。不会扫描或删除 Windows\\Installer、Visual Studio 包缓存、Package Cache、WinSxS、Windows.old 或正在使用的程序文件。",
				"Candidate roots are built from installed-application uninstall metadata (InstallLocation/DisplayIcon) and explicit app/vendor updater-staging locations. Files are then filtered by installer extensions, installer/update/driver naming markers, and the meaning of the containing staging directory. Only candidates of at least 20 MB are listed; there is no whole-drive filename guessing, and every file requires manual selection and confirmation. Windows\\Installer, Visual Studio package cache, Package Cache, WinSxS, Windows.old, and active program files are never scanned or deleted."),
			["packages_none"] = new("没有找到符合保守条件的旧安装包/升级包候选。", "No old installer/update candidates matched the conservative rules."),
			["packages_columns_source"] = new("来源", "Source"),
			["packages_columns_file"] = new("文件", "File"),
			["packages_columns_date"] = new("最后变化", "Last change"),
			["packages_columns_size"] = new("大小", "Size"),
			["packages_select"] = new("选择要删除的安装包（逐项审核）", "Select installer files to delete (review each item)"),
			["packages_confirm"] = new("确定删除选中的 {0} 个安装包/升级包（约 {1}）吗？删除后无法通过本工具恢复。", "Delete the selected {0} installer/update file(s) (about {1})? This tool cannot restore them."),
			["packages_deleted"] = new("已删除 {0} 个安装包/升级包，释放约 {1}。", "Deleted {0} installer/update file(s), freeing about {1}."),
			["migration_title"] = new("C 盘数据迁移", "C: data migration"),
			["migration_note"] = new(
				"迁移只提供可明确修改配置或环境变量的目录。用户目录使用 Windows 已知文件夹设置；开发工具使用各自的缓存/包目录配置；目前只在检测到微信 FileSavePath、CODEX_HOME、HF_HOME 或 OLLAMA_MODELS 等已确认路径时提供软件数据迁移。QQ、Discord、Slack、Teams、ChatGPT/Claude Desktop、Cursor、Windsurf 等没有确认配置路径的软件不会自动列出。复制成功并完成配置后才会清理原目录，目标盘必须有足够空间；正在运行的相关软件会被跳过。",
				"Migration is offered only for directories whose configuration or environment-variable path is known. User folders use Windows known-folder settings; developer tools use their documented cache/package settings; app data is offered only when a confirmed path such as WeChat FileSavePath, CODEX_HOME, HF_HOME, or OLLAMA_MODELS is detected. Apps such as QQ, Discord, Slack, Teams, ChatGPT/Claude Desktop, Cursor, and Windsurf are not listed without a confirmed configurable path. The source is cleaned only after copying and applying configuration successfully. The destination needs enough free space, and targets used by running applications are skipped."),
			["migration_none"] = new("没有找到可安全迁移且当前存在的目录。未确认配置路径的软件不会自动列出。", "No existing directory with a confirmed safe migration scheme was found. Apps without a confirmed configurable path are not offered automatically."),
			["migration_columns_category"] = new("类别", "Category"),
			["migration_columns_name"] = new("项目", "Item"),
			["migration_columns_source"] = new("当前路径", "Current path"),
			["migration_columns_plan"] = new("迁移方案", "Migration scheme"),
			["migration_columns_destination"] = new("目标路径", "Destination"),
			["migration_columns_config"] = new("配置更新", "Configuration update"),
			["migration_columns_size"] = new("大小", "Size"),
			["migration_select"] = new("选择要迁移的目录（可多选）", "Select directories to migrate (multi-select)"),
			["migration_drive_select"] = new("选择目标盘符（不会使用当前源盘）", "Select a destination drive (the source drive is excluded)"),
			["migration_no_drives"] = new("没有找到可用的其他固定磁盘。请连接目标磁盘后重试。", "No other ready fixed drive was found. Connect a destination drive and try again."),
			["migration_no_valid_targets"] = new("所选目录没有形成可执行的迁移计划。请检查路径、空间或相关软件是否正在运行。", "The selected directories did not produce an executable migration plan. Check paths, free space, or running applications."),
			["migration_confirm"] = new("确定迁移 {0} 个目录（约 {1}），并在配置更新成功后清理原目录吗？", "Migrate {0} selected item(s) (about {1}) and clean the source only after configuration succeeds?"),
			["migration_plan_warning"] = new("迁移会复制数据、修改列出的配置或用户环境变量，并尝试删除原目录中的内容；请先关闭相关软件。环境变量对新启动的终端/程序生效。", "Migration copies data, changes the listed configuration or user environment variable, and then attempts to remove the source contents; close related applications first. Environment-variable changes affect newly started terminals and applications."),
			["migration_running"] = new("正在迁移选中的目录...", "Migrating selected directories..."),
			["migration_done"] = new("迁移完成：处理 {0} 个目录，迁移约 {1}。", "Migration complete: processed {0} item(s), migrating about {1}."),
			["migration_failed"] = new("有 {0} 个目录迁移失败或未完成。目标目录会保留以便恢复。", "{0} item(s) failed or were incomplete. Destination data is kept for recovery."),
			["help_body"] = new(
				"会处理：用户/Windows 临时文件、Windows 更新下载和 Delivery Optimization 缓存、错误报告、缩略图/驱动着色器、常见浏览器和应用缓存、微信/QQ/聊天通讯软件缓存、抖音/B站/视频直播软件缓存、游戏启动器缓存、旧 Windows 诊断日志，以及可选的开发工具和 AI 工具缓存。\n\n" +
				"旧安装包/升级包只扫描软件自有更新暂存目录和注册表关联的已安装软件路径，逐文件确认；用户个人文件夹不会被默认操作。注册表只清理 HKCU 使用记录，卸载项失效路径只显示候选。\n\n" +
				"重复文件功能只列表展示，不会删除。卸载应用残留必须人工多选并确认；删除前会再次检查是否仍像已安装应用。C 盘数据迁移单独执行，只提供已确认有配置路径的用户目录、开发工具目录和软件数据。\n\n" +
				"不会处理：Windows\\Installer、WinSxS、Windows.old、DriverStore、正在使用的驱动、服务、页面文件、系统还原点、浏览器历史/登录信息、聊天数据库/媒体、视频下载/离线缓存、AI 模型和密钥、系统关键注册表或未知应用数据。",
				"Handled: user/Windows temporary files, Windows Update and Delivery Optimization caches, error reports, thumbnail/driver shader caches, common browser/app caches, WeChat/QQ/chat caches, Douyin/Bilibili/video/live-app caches, game-launcher caches, old Windows diagnostics, and optional developer/AI tool caches.\n\n" +
				"Old installers/updates are scanned only in app-owned updater staging folders and paths linked to installed applications, with per-file confirmation; user-owned folders are not touched by default. HKCU usage history can be cleared, while missing uninstall paths are review-only.\n\n" +
				"Duplicate-file mode is display-only and never deletes. Uninstalled-app residue requires manual multi-selection and confirmation, with a second installed-app safety check before deletion. C: data migration is separate and only offers user folders, developer directories, and app data with a confirmed configuration path.\n\n" +
				"Never handled: Windows\\Installer, WinSxS, Windows.old, DriverStore, active drivers, services, page files, restore points, browser history/login data, chat databases/media, downloaded/offline video, AI models/credentials, system-critical registry data, or unknown app data."),
			["usage_header"] = new("命令行用法", "Command-line usage"),
			["exit_message"] = new("已退出。", "Exited."),
		};

	public Localizer(AppLanguage language)
	{
		Language = language;
	}

	public AppLanguage Language { get; }

	public string Get(string key)
		=> Values.TryGetValue(key, out var value) ? value.Get(Language) : key;

	public string Format(string key, params object[] arguments)
		=> string.Format(CultureInfo.InvariantCulture, Get(key), arguments);
}

public static class LanguageDetector
{
	public static AppLanguage Detect()
	{
		// Windows maps the current display language to CurrentUICulture. The installed
		// UI culture is only a fallback for hosts that do not provide a current culture.
		var culture = CultureInfo.CurrentUICulture;
		if (string.IsNullOrWhiteSpace(culture.Name))
		{
			culture = CultureInfo.InstalledUICulture;
		}

		return culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
			? AppLanguage.Chinese
			: AppLanguage.English;
	}
}
