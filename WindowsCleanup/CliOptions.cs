using WindowsCleanup.Localization;
using Spectre.Console;

namespace WindowsCleanup;

public enum CliCommand
{
	Interactive,
	Help,
	Scan,
	Clean,
	Registry,
	Duplicates,
	Remnants,
	Packages
}

public sealed record CliOptions(
	CliCommand Command,
	bool DryRun,
	bool SelectAll);

public static class CliOptionsParser
{
	public static bool TryParse(string[] args, out CliOptions options, out string? error)
	{
		var command = CliCommand.Interactive;
		var dryRun = false;
		var selectAll = false;
		error = null;

		for (var index = 0; index < args.Length; index++)
		{
			var argument = args[index].Trim();
			switch (argument.ToLowerInvariant())
			{
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

		options = new CliOptions(command, dryRun, selectAll);
		return true;
	}

	public static void WriteUsage(Localizer text)
	{
		AnsiConsole.Write(new Rule(text.Get("usage_header")));
		AnsiConsole.WriteLine("winclean [options]");
		AnsiConsole.WriteLine();
		AnsiConsole.WriteLine(text.Language == AppLanguage.Chinese
			? "选项："
			: "Options:");
		AnsiConsole.WriteLine("  --scan              " + (text.Language == AppLanguage.Chinese ? "只扫描并显示发现的项目" : "Scan and display found items only"));
		AnsiConsole.WriteLine("  --clean             " + (text.Language == AppLanguage.Chinese ? "交互式选择并清理" : "Interactively select and clean"));
		AnsiConsole.WriteLine("  --clean --all       " + (text.Language == AppLanguage.Chinese ? "选择默认安全项目（仍需确认）" : "Select default-safe items (confirmation still required)"));
		AnsiConsole.WriteLine("  --registry           " + (text.Language == AppLanguage.Chinese ? "预览并清理当前用户注册表记录" : "Preview and clean current-user registry history"));
		AnsiConsole.WriteLine("  --duplicates         " + (text.Language == AppLanguage.Chinese ? "查找并逐组审核重复文件" : "Find and review duplicate files by group"));
		AnsiConsole.WriteLine("  --remnants           " + (text.Language == AppLanguage.Chinese ? "显示卸载应用残留候选" : "Show uninstalled-app residue candidates"));
		AnsiConsole.WriteLine("  --packages           " + (text.Language == AppLanguage.Chinese ? "审核旧安装包/升级包候选" : "Review old installer/update package candidates"));
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
