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
				"安全边界：不删除 WinSxS、Windows\\Installer、驱动、服务配置或系统关键注册表；有风险的项目默认不纳入 --all。",
				"Safety boundary: WinSxS, Windows\\Installer, drivers, services, and system-critical registry data are never deleted; review-only items are excluded from --all."),
			["menu_title"] = new("请选择操作", "Choose an action"),
			["menu_clean"] = new("清理 C 盘安全项目（临时文件和缓存）", "Clean safe C: items (temporary files and caches)"),
			["menu_registry"] = new("清理当前用户注册表使用记录", "Clean current-user registry history"),
			["menu_duplicates"] = new("查找重复文件（人工确认删除）", "Find duplicate files (review before deletion)"),
			["menu_remnants"] = new("查看卸载应用残留候选（仅审核）", "Review uninstalled-app residue candidates"),
			["menu_packages"] = new("审核旧安装包/升级包（逐项确认）", "Review old installers/updates (confirm per item)"),
			["menu_help"] = new("查看安全说明和命令行用法", "Show safety notes and command-line usage"),
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
			["cleanup_all_skipped_opt_in"] = new("--all 已跳过需要额外判断的项目；如需处理，请使用交互式 --clean 手动选择。", "--all skipped items requiring extra review; use interactive --clean to select them manually."),
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
				"只会修改 HKCU 中的最近打开、运行、搜索、路径、应用启动和兼容性助手历史，以及可重建的应用显示名称缓存。HKCU/HKLM 的卸载项只做路径失效候选审核，不会自动删除注册表项。",
				"Only HKCU recent-open, run, search, path, app-launch, Compatibility Assistant history, and regenerable display-name cache can be modified. HKCU/HKLM uninstall entries are checked for missing paths only and are never deleted automatically."),
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
			["duplicates_roots"] = new("扫描目录（仅限当前用户常用文件夹）：", "Directories scanned (common folders of the current user):"),
			["duplicates_scanning"] = new("正在计算文件指纹，较大的目录可能需要一些时间...", "Calculating file fingerprints; large folders may take a while..."),
			["duplicates_none"] = new("没有找到完全相同的重复文件。", "No byte-identical duplicate files were found."),
			["duplicates_found"] = new("找到 {0} 组重复文件。每组默认保留第一份：", "Found {0} duplicate group(s). The first file in each group is kept:"),
			["duplicates_group"] = new("第 {0} 组：{1}，共 {2} 份", "Group {0}: {1}, {2} copies"),
			["duplicates_keep"] = new("保留：{0}", "Keep: {0}"),
			["duplicates_select_keep"] = new("请选择这一组要保留的文件", "Choose the file to keep in this group"),
			["duplicates_delete_confirm"] = new("删除这一组中除保留文件外的 {0} 份吗？", "Delete the {0} duplicate copy/copies in this group?"),
			["duplicates_deleted"] = new("已删除 {0} 份重复文件，释放约 {1}。", "Deleted {0} duplicate copy/copies, freeing about {1}."),
			["duplicates_skipped"] = new("已跳过这一组。", "Skipped this group."),
			["duplicates_limit"] = new("只显示前 {0} 组；可缩小扫描目录后再次运行。", "Only the first {0} groups are shown; scan narrower folders to see more."),
			["remnants_title"] = new("卸载应用残留候选", "Uninstalled-app residue candidates"),
			["remnants_note"] = new(
				"Windows 没有可靠的机制判断 AppData 文件夹是否真的已卸载且无用，因此这里仅列出长期未变化的候选，不会自动删除。请在确认软件已卸载并备份后手动处理。",
				"Windows has no reliable way to prove that an AppData folder belongs to an uninstalled and unused app. Candidates are listed only; nothing is deleted automatically. Verify and back up before handling them manually."),
			["remnants_none"] = new("没有找到符合保守规则的残留候选。", "No candidates matched the conservative rules."),
			["remnants_columns_path"] = new("路径", "Path"),
			["remnants_columns_date"] = new("最后变化", "Last change"),
			["remnants_columns_size"] = new("大小", "Size"),
			["remnants_manual"] = new("这些项目仅供人工审核，工具不会自动删除。", "These items are for manual review only; the tool will not delete them."),
			["packages_title"] = new("旧安装包和升级包候选", "Old installer and update package candidates"),
			["packages_note"] = new(
				"只扫描当前用户 Downloads，以及 AMD/Intel/NVIDIA 常见安装包暂存目录（包括 NVIDIA Installer2）；仅列出超过 30 天且至少 20 MB 的安装包候选。每个文件都必须人工勾选并再次确认。不会扫描或删除 Windows\\Installer、WinSxS、Windows.old 或正在使用的程序文件。",
				"Only current-user Downloads and common AMD/Intel/NVIDIA installer staging folders (including NVIDIA Installer2) are scanned. Candidates must be at least 20 MB and 30 days old. Every file requires manual selection and confirmation. Windows\\Installer, WinSxS, Windows.old, and active program files are never scanned or deleted."),
			["packages_none"] = new("没有找到符合保守条件的旧安装包/升级包候选。", "No old installer/update candidates matched the conservative rules."),
			["packages_columns_source"] = new("来源", "Source"),
			["packages_columns_file"] = new("文件", "File"),
			["packages_columns_date"] = new("最后变化", "Last change"),
			["packages_columns_size"] = new("大小", "Size"),
			["packages_select"] = new("选择要删除的安装包（逐项审核）", "Select installer files to delete (review each item)"),
			["packages_confirm"] = new("确定删除选中的 {0} 个安装包/升级包（约 {1}）吗？删除后无法通过本工具恢复。", "Delete the selected {0} installer/update file(s) (about {1})? This tool cannot restore them."),
			["packages_deleted"] = new("已删除 {0} 个安装包/升级包，释放约 {1}。", "Deleted {0} installer/update file(s), freeing about {1}."),
			["help_title"] = new("安全边界", "Safety boundary"),
			["help_body"] = new(
				"会处理：用户/Windows 临时文件、Windows 更新下载和 Delivery Optimization 缓存、错误报告、缩略图/驱动着色器、常见浏览器和应用缓存、微信/QQ/聊天通讯软件缓存、抖音/B站/直播软件缓存、启动器缓存、旧 CBS/DISM 日志，以及可选的开发工具和 AI 工具缓存。\n\n" +
				"旧安装包/升级包只扫描 Downloads 和常见厂商暂存目录，逐文件确认；注册表只清理 HKCU 使用记录，卸载项失效路径只显示候选。\n\n" +
				"不会处理：Windows\\Installer、WinSxS、Windows.old、DriverStore、正在使用的驱动、服务、页面文件、系统还原点、浏览器历史/登录信息、聊天数据库/媒体、视频下载/离线缓存、AI 模型和密钥、系统关键注册表或未知应用数据。",
				"Handled: user/Windows temporary files, Windows Update and Delivery Optimization caches, error reports, thumbnail/driver shader caches, common browser/app caches, WeChat/QQ/chat caches, Douyin/Bilibili/live-app caches, launcher caches, old CBS/DISM logs, and optional developer/AI tool caches.\n\n" +
				"Old installers/updates are scanned only in Downloads and common vendor staging folders and require per-file confirmation; HKCU usage history can be cleared, while missing uninstall paths are review-only.\n\n" +
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
