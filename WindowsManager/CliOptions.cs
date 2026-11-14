using WindowsManager.Localization;
using Spectre.Console;

namespace WindowsManager;

public enum CliCommand
{
	Interactive,
	Help,
	Scan,
	Clean,
	Registry,
	Duplicates,
	Remnants,
	Packages,
	Migration,
	LargeFiles,
	Developer,
	DeveloperPackages,
	Containers,
	ContainerReview,
	Hardware
}

public sealed record CliOptions(
	CliCommand Command,
	bool DryRun,
	bool SelectAll,
	IReadOnlyList<string>? ProjectRoots = null);

public static class CliOptionsParser
{
	public static bool TryParse(string[] args, out CliOptions options, out string? error)
	{
		var command = CliCommand.Interactive;
		var dryRun = false;
		var selectAll = false;
		var projectRoots = new List<string>();
		error = null;

		for (var index = 0; index < args.Length; index++)
		{
			var argument = args[index].Trim();
			switch (argument.ToLowerInvariant())
			{
				case "--containers":
					if (!TrySetCommand(CliCommand.Containers, ref command, ref error))
					{
						options = default!;
						return false;
					}
					break;
				case "--containers-review":
					if (!TrySetCommand(CliCommand.ContainerReview, ref command, ref error))
					{
						options = default!;
						return false;
					}
					break;
				case "--hardware":
					if (!TrySetCommand(CliCommand.Hardware, ref command, ref error))
					{
						options = default!;
						return false;
					}
					break;
				case "--developer-packages":
					if (!TrySetCommand(CliCommand.DeveloperPackages, ref command, ref error))
					{
						options = default!;
						return false;
					}
					break;
				case "--developer":
					if (!TrySetCommand(CliCommand.Developer, ref command, ref error))
					{
						options = default!;
						return false;
					}
					break;
				case "--project-root":
					if (++index >= args.Length || !Cleanup.DeveloperCacheCatalog.IsCachePath(args[index]) || !Directory.Exists(args[index]))
					{
						error = "--project-root requires an existing absolute project directory (not a broad user/system root, Downloads or a link).";
						options = default!;
						return false;
					}
					projectRoots.Add(Path.GetFullPath(args[index]));
					break;
				case "-h":
				case "--help":
				case "/?":
					if (!TrySetCommand(CliCommand.Help, ref command, ref error))
					{
						options = default!;
						return false;
					}

					break;

				case "--scan":
					if (!TrySetCommand(CliCommand.Scan, ref command, ref error))
					{
						options = default!;
						return false;
					}

					break;

				case "--clean":
					if (!TrySetCommand(CliCommand.Clean, ref command, ref error))
					{
						options = default!;
						return false;
					}

					break;

				case "--all":
				case "--clean-all":
					if (command != CliCommand.Interactive && command != CliCommand.Clean)
					{
						error = "--all can only be used with --clean.";
						options = default!;
						return false;
					}

					command = CliCommand.Clean;
					selectAll = true;
					break;

				case "--registry":
					if (!TrySetCommand(CliCommand.Registry, ref command, ref error))
					{
						options = default!;
						return false;
					}

					break;

				case "--duplicates":
					if (!TrySetCommand(CliCommand.Duplicates, ref command, ref error))
					{
						options = default!;
						return false;
					}

					break;

				case "--remnants":
					if (!TrySetCommand(CliCommand.Remnants, ref command, ref error))
					{
						options = default!;
						return false;
					}

					break;

				case "--packages":
				case "--installers":
					if (!TrySetCommand(CliCommand.Packages, ref command, ref error))
					{
						options = default!;
						return false;
					}

					break;

				case "--migration":
				case "--migrate":
					if (!TrySetCommand(CliCommand.Migration, ref command, ref error))
					{
						options = default!;
						return false;
					}

					break;

				case "--large-files":
				case "--large-file-analysis":
					if (!TrySetCommand(CliCommand.LargeFiles, ref command, ref error))
					{
						options = default!;
						return false;
					}

					break;

				case "--dry-run":
					dryRun = true;
					break;

				default:
					error = $"Unknown argument: {argument}";
					options = default!;
					return false;
			}
		}

		if (dryRun && command == CliCommand.Interactive)
		{
			command = CliCommand.Scan;
		}

		if (projectRoots.Count > 0 && command != CliCommand.Developer)
		{
			error = "--project-root can only be used with --developer.";
			options = default!;
			return false;
		}
		options = new CliOptions(command, dryRun, selectAll, projectRoots);
		return true;
	}

	public static void WriteUsage(Localizer text)
	{
		AnsiConsole.Write(new Rule(text.Get("usage_header")));
		AnsiConsole.WriteLine("windowsmanager [options]");
		AnsiConsole.WriteLine("  --hardware          " + (text.Language == AppLanguage.Chinese ? "检测并显示硬件型号与性能指标" : "Show hardware models and performance metrics"));
		AnsiConsole.WriteLine("  --containers        " + (text.Language == AppLanguage.Chinese ? "清理无标签且未被引用的镜像" : "Review dangling and unreferenced images"));
		AnsiConsole.WriteLine("  --containers-review " + (text.Language == AppLanguage.Chinese ? "单独审核有标签镜像和未引用卷" : "Separately review tagged images and unreferenced volumes"));
		AnsiConsole.WriteLine("  --developer [--project-root <absolute-path>] [--dry-run]");
		AnsiConsole.WriteLine();
		AnsiConsole.WriteLine(text.Language == AppLanguage.Chinese
			? "选项："
			: "Options:");
		AnsiConsole.WriteLine("  --scan              " + (text.Language == AppLanguage.Chinese ? "只扫描并显示发现的项目" : "Scan and display found items only"));
		AnsiConsole.WriteLine("  --developer         " + (text.Language == AppLanguage.Chinese ? "项目构建中间产物和最终输出（保留包缓存）" : "Project intermediate and final build outputs (keep package caches)"));
		AnsiConsole.WriteLine("  --developer-packages " + (text.Language == AppLanguage.Chinese ? "单独清理包下载/依赖缓存，可能需要重新下载" : "Separately clean package/dependency caches; downloads may be required"));
		AnsiConsole.WriteLine("  --project-root PATH " + (text.Language == AppLanguage.Chinese ? "指定开发目录，可重复；仅与 --developer 使用" : "Project directory; repeatable, requires --developer"));
		AnsiConsole.WriteLine("  --clean             " + (text.Language == AppLanguage.Chinese ? "交互式选择并清理" : "Interactively select and clean"));
		AnsiConsole.WriteLine("  --clean --all       " + (text.Language == AppLanguage.Chinese ? "选择默认安全项目（仍需确认）" : "Select default-safe items (confirmation still required)"));
		AnsiConsole.WriteLine("  --registry           " + (text.Language == AppLanguage.Chinese ? "预览并清理当前用户注册表记录" : "Preview and clean current-user registry history"));
		AnsiConsole.WriteLine("  --duplicates         " + (text.Language == AppLanguage.Chinese ? "查找并列表展示重复文件（不删除）" : "Find and list duplicate files (no deletion)"));
		AnsiConsole.WriteLine("  --remnants           " + (text.Language == AppLanguage.Chinese ? "审核并可多选清理卸载应用残留" : "Review and multi-select uninstalled-app residue cleanup"));
		AnsiConsole.WriteLine("  --packages           " + (text.Language == AppLanguage.Chinese ? "审核旧安装包/升级包候选" : "Review old installer/update package candidates"));
		AnsiConsole.WriteLine("  --migration          " + (text.Language == AppLanguage.Chinese ? "迁移 C 盘用户、开发工具和支持的软件数据" : "Migrate C: user, developer, and supported app data"));
		AnsiConsole.WriteLine("  --large-files        " + (text.Language == AppLanguage.Chinese ? "分析 C 盘非系统目录中的大文件并可多选删除" : "Analyze large files outside system directories on C: and optionally delete selected files"));
		AnsiConsole.WriteLine("  --dry-run            " + (text.Language == AppLanguage.Chinese ? "预览，不执行删除/修改" : "Preview without deleting or modifying"));
		AnsiConsole.WriteLine("  -h, --help           " + (text.Language == AppLanguage.Chinese ? "显示帮助" : "Show help"));
		AnsiConsole.WriteLine();
		AnsiConsole.WriteLine(text.Get("help_body"));
	}

	private static bool TrySetCommand(CliCommand next, ref CliCommand current, ref string? error)
	{
		if (current != CliCommand.Interactive && current != next)
		{
			error = "Only one operation command can be selected.";
			return false;
		}

		current = next;
		return true;
	}

}
