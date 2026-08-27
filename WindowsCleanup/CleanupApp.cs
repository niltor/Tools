using System.Security.Principal;
using Spectre.Console;
using WindowsCleanup.Cleanup;
using WindowsCleanup.Localization;
using WindowsCleanup.Registry;

namespace WindowsCleanup;

public sealed class CleanupApp
{
	private readonly Localizer _text;

	public CleanupApp(Localizer text)
	{
		_text = text;
	}

	public async Task<int> RunAsync(CliOptions options)
	{
		if (options.Command == CliCommand.Help)
		{
			CliOptionsParser.WriteUsage(_text);
			return 0;
		}

		if (!OperatingSystem.IsWindows())
		{
			WriteError(_text.Get("not_windows"));
			return 1;
		}

		if (options.Command == CliCommand.Interactive)
		{
			return await RunInteractiveAsync().ConfigureAwait(false);
		}

		ShowBanner();
		return options.Command switch
		{
			CliCommand.Scan => RunSafeCleanup(selectAll: false, dryRun: true),
			CliCommand.Clean => RunSafeCleanup(options.SelectAll, options.DryRun),
			CliCommand.Registry => RunRegistryCleanup(options.DryRun),
			CliCommand.Duplicates => await RunDuplicatesAsync(options.DryRun).ConfigureAwait(false),
			CliCommand.Remnants => RunRemnants(),
			CliCommand.Packages => RunInstallerCandidates(options.DryRun),
			_ => 0
		};
	}

	private async Task<int> RunInteractiveAsync()
	{
		ShowBanner();
		while (true)
		{
			var choices = new[]
			{
				new MenuEntry("clean", _text.Get("menu_clean")),
				new MenuEntry("registry", _text.Get("menu_registry")),
				new MenuEntry("duplicates", _text.Get("menu_duplicates")),
				new MenuEntry("remnants", _text.Get("menu_remnants")),
				new MenuEntry("packages", _text.Get("menu_packages")),
				new MenuEntry("help", _text.Get("menu_help")),
				new MenuEntry("exit", _text.Get("menu_exit"))
			};

			var selected = AnsiConsole.Prompt(
				new SelectionPrompt<MenuEntry>()
					.Title(_text.Get("menu_title"))
					.PageSize(10)
					.AddChoices(choices));

			switch (selected.Id)
			{
				case "clean":
					RunSafeCleanup(selectAll: false, dryRun: false);
					break;
				case "registry":
					RunRegistryCleanup(dryRun: false);
					break;
				case "duplicates":
					await RunDuplicatesAsync(dryRun: false).ConfigureAwait(false);
					break;
				case "remnants":
					RunRemnants();
					break;
				case "packages":
					RunInstallerCandidates(dryRun: false);
					break;
				case "help":
					ShowHelp();
					break;
				case "exit":
					WriteSuccess(_text.Get("exit_message"));
					return 0;
			}

			AnsiConsole.WriteLine();
		}
	}

	private int RunSafeCleanup(bool selectAll, bool dryRun)
	{
		var targets = CleanupCatalog.Build();
		IReadOnlyList<CleanupTarget> available = Array.Empty<CleanupTarget>();
		AnsiConsole.Status().Start(_text.Get("scanning"), status =>
		{
			available = CleanupScanner.ScanTargets(
				targets,
				CancellationToken.None,
				target => status.Status($"{_text.Get("scanning")} {target.Name.Get(_text.Language)}"));
		});

		AnsiConsole.MarkupLine($"[green]{Markup.Escape(_text.Get("scan_complete"))}[/]");
		PrintCleanupPlan(available);
		if (available.Count == 0)
		{
			return 0;
		}

		if (dryRun)
		{
			WriteWarning(_text.Get("dry_run"));
			return 0;
		}

		if (!IsAdministrator())
		{
			WriteWarning(_text.Get("not_admin"));
		}
		else
		{
			WriteSuccess(_text.Get("admin_available"));
		}

		IReadOnlyList<CleanupTarget> selected;
		if (selectAll)
		{
			selected = available
				.Where(target => !target.OptIn && target.Risk == CleanupRisk.Safe)
				.ToArray();
			if (selected.Count != available.Count)
			{
				WriteWarning(_text.Get("cleanup_all_skipped_opt_in"));
			}
		}
		else
		{
			var choices = available.Select(target => new CleanupChoice(target, _text.Language)).ToArray();
			var selectedChoices = AnsiConsole.Prompt(
				new MultiSelectionPrompt<CleanupChoice>()
					.Title(_text.Get("cleanup_select"))
					.PageSize(15)
					.InstructionsText(_text.Get("cleanup_select_instructions"))
					.AddChoices(choices));
			selected = selectedChoices.Select(choice => choice.Target).ToArray();
		}

		if (selected.Count == 0)
		{
			WriteWarning(_text.Get("cleanup_none_selected"));
			return 0;
		}

		if (selected.Any(target => target.Risk == CleanupRisk.Moderate))
		{
			WriteWarning(_text.Get("cleanup_moderate_warning"));
		}

		var totalBytes = selected.Sum(target => target.Snapshot.Bytes);
		if (!AnsiConsole.Confirm(_text.Format("cleanup_confirm", selected.Count, FormatBytes(totalBytes)), defaultValue: false))
		{
			WriteWarning(_text.Get("cleanup_cancelled"));
			return 0;
		}

		var totalDeletedFiles = 0;
		long totalDeletedBytes = 0;
		foreach (var target in selected)
		{
			CleanupExecutionResult result = CleanupExecutionResult.Empty;
			AnsiConsole.Status().Start(_text.Format("cleaning_item", target.Name.Get(_text.Language)), _ =>
			{
				result = CleanupExecutor.Execute(target);
			});

			totalDeletedFiles += result.DeletedFiles;
			totalDeletedBytes += result.DeletedBytes;
			if (result.FailedFiles > 0)
			{
				WriteWarning(_text.Format("cleanup_failed", result.FailedFiles));
				PrintErrors(result.Errors);
			}
		}

		WriteSuccess(_text.Format("cleanup_result", totalDeletedFiles, FormatBytes(totalDeletedBytes)));
		return 0;
	}

	private int RunRegistryCleanup(bool dryRun)
	{
		var cleaner = new RegistryCleaner();
		AnsiConsole.Write(new Panel(new Text(_text.Get("registry_warning")))
		{
			Header = new PanelHeader(_text.Get("registry_title")),
			Border = BoxBorder.Rounded
		});
		AnsiConsole.WriteLine();

		var previews = cleaner.Preview();
		var orphanCandidates = cleaner.FindOrphanedUninstallEntries();
		if (previews.Count == 0 && orphanCandidates.Count == 0)
		{
			WriteSuccess(_text.Get("registry_empty"));
			return 0;
		}

		if (previews.Count > 0)
		{
			var table = new Table { Border = TableBorder.Rounded };
			table.AddColumn(_text.Get("cleanup_columns_category"));
			table.AddColumn(_text.Get("registry_columns_path"));
			table.AddColumn(_text.Get("registry_columns_entries"));
			foreach (var preview in previews)
			{
				table.AddRow(
					new Text(preview.Target.Name.Get(_text.Language)),
					new Text($"HKCU\\{preview.Target.Path}"),
					new Text(preview.EntryCount.ToString()));
			}

			AnsiConsole.Write(table);
			AnsiConsole.WriteLine();
		}

		if (orphanCandidates.Count > 0)
		{
			AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(_text.Get("registry_orphan_header"))}[/]");
			var orphanTable = new Table { Border = TableBorder.Rounded };
			orphanTable.AddColumn(_text.Get("registry_orphan_columns_name"));
			orphanTable.AddColumn(_text.Get("registry_orphan_columns_location"));
			orphanTable.AddColumn(_text.Get("registry_orphan_columns_hive"));
			foreach (var candidate in orphanCandidates)
			{
				orphanTable.AddRow(
					new Text(candidate.DisplayName),
					new Text(candidate.InstallLocation),
					new Text($"{candidate.Hive} ({candidate.View})"));
			}

			AnsiConsole.Write(orphanTable);
			WriteWarning(_text.Get("registry_orphan_manual"));
			AnsiConsole.WriteLine();
		}

		if (previews.Count == 0)
		{
			WriteWarning(_text.Get("registry_history_empty_orphan_only"));
			return 0;
		}

		if (dryRun)
		{
			WriteWarning(_text.Get("registry_dry_run"));
			return 0;
		}

		if (!AnsiConsole.Confirm(_text.Get("registry_confirm"), defaultValue: false))
		{
			WriteWarning(_text.Get("cleanup_cancelled"));
			return 0;
		}

		var result = cleaner.Clear(previews);
		WriteSuccess(_text.Format("registry_done") + $" ({result.ClearedEntries})");
		if (result.FailedTargets > 0)
		{
			WriteWarning(_text.Format("registry_failed", result.FailedTargets));
			PrintErrors(result.Errors);
		}

		return result.FailedTargets == 0 ? 0 : 1;
	}

	private async Task<int> RunDuplicatesAsync(bool dryRun)
	{
		AnsiConsole.Write(new Rule(_text.Get("duplicates_title")));
		var roots = DuplicateFinder.GetDefaultRoots();
		AnsiConsole.MarkupLine($"[grey]{Markup.Escape(_text.Get("duplicates_roots"))}[/]");
		foreach (var root in roots)
		{
			AnsiConsole.WriteLine($"  {root}");
		}

		if (roots.Count == 0)
		{
			WriteWarning(_text.Get("duplicates_none"));
			return 0;
		}

		AnsiConsole.WriteLine();
		WriteWarning(_text.Get("duplicates_scanning"));
		var finder = new DuplicateFinder();
		var groups = await finder.FindAsync(roots).ConfigureAwait(false);
		if (groups.Count == 0)
		{
			WriteSuccess(_text.Get("duplicates_none"));
			return 0;
		}

		const int maximumDisplayedGroups = 30;
		var displayedGroups = groups.Take(maximumDisplayedGroups).ToArray();
		WriteSuccess(_text.Format("duplicates_found", groups.Count));
		if (groups.Count > maximumDisplayedGroups)
		{
			WriteWarning(_text.Format("duplicates_limit", maximumDisplayedGroups));
		}

		var groupNumber = 0;
		foreach (var group in displayedGroups)
		{
			groupNumber++;
			AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(_text.Format("duplicates_group", groupNumber, FormatBytes(group.Size), group.Files.Count))}[/]");
			if (dryRun)
			{
				AnsiConsole.WriteLine($"  {_text.Format("duplicates_keep", group.Files[0])}");
			}

			var pathsToDisplay = dryRun ? group.Files.Skip(1) : group.Files;
			foreach (var duplicate in pathsToDisplay)
			{
				AnsiConsole.WriteLine($"  {duplicate}");
			}

			if (dryRun)
			{
				continue;
			}

			var keep = AnsiConsole.Prompt(
				new SelectionPrompt<DuplicateChoice>()
					.Title(_text.Get("duplicates_select_keep"))
					.PageSize(10)
					.AddChoices(group.Files.Select(path => new DuplicateChoice(path))))
				.Path;
			var reviewedGroup = group with
			{
				Files = new[] { keep }.Concat(group.Files.Where(path => !string.Equals(path, keep, StringComparison.OrdinalIgnoreCase))).ToArray()
			};
			AnsiConsole.WriteLine($"  {_text.Format("duplicates_keep", keep)}");

			if (!AnsiConsole.Confirm(_text.Format("duplicates_delete_confirm", reviewedGroup.Files.Count - 1), defaultValue: false))
			{
				WriteWarning(_text.Get("duplicates_skipped"));
				continue;
			}

			var result = DuplicateFinder.DeleteDuplicates(reviewedGroup);
			WriteSuccess(_text.Format("duplicates_deleted", result.DeletedFiles, FormatBytes(result.DeletedBytes)));
			if (result.FailedFiles > 0)
			{
				WriteWarning(_text.Format("cleanup_failed", result.FailedFiles));
				PrintErrors(result.Errors);
			}
		}

		return 0;
	}

	private int RunRemnants()
	{
		AnsiConsole.Write(new Panel(new Text(_text.Get("remnants_note")))
		{
			Header = new PanelHeader(_text.Get("remnants_title")),
			Border = BoxBorder.Rounded
		});
		AnsiConsole.WriteLine();

		var candidates = StaleAppDataScanner.Find();
		if (candidates.Count == 0)
		{
			WriteSuccess(_text.Get("remnants_none"));
			return 0;
		}

		var table = new Table { Border = TableBorder.Rounded };
		table.AddColumn(_text.Get("remnants_columns_path"));
		table.AddColumn(_text.Get("remnants_columns_date"));
		table.AddColumn(_text.Get("remnants_columns_size"));
		foreach (var candidate in candidates)
		{
			table.AddRow(
				new Text(candidate.Path),
				new Text(candidate.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
				new Text(FormatBytes(candidate.Bytes)));
		}

		AnsiConsole.Write(table);
		WriteWarning(_text.Get("remnants_manual"));
		return 0;
	}

	private int RunInstallerCandidates(bool dryRun)
	{
		AnsiConsole.Write(new Panel(new Text(_text.Get("packages_note")))
		{
			Header = new PanelHeader(_text.Get("packages_title")),
			Border = BoxBorder.Rounded
		});
		AnsiConsole.WriteLine();

		var candidates = InstallerCandidateScanner.Find();
		if (candidates.Count == 0)
		{
			WriteSuccess(_text.Get("packages_none"));
			return 0;
		}

		var table = new Table { Border = TableBorder.Rounded };
		table.AddColumn(_text.Get("packages_columns_source"));
		table.AddColumn(_text.Get("packages_columns_file"));
		table.AddColumn(_text.Get("packages_columns_date"));
		table.AddColumn(_text.Get("packages_columns_size"));
		foreach (var candidate in candidates)
		{
			table.AddRow(
				new Text(candidate.Source.Get(_text.Language)),
				new Text(candidate.Path),
				new Text(candidate.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
				new Text(FormatBytes(candidate.Bytes)));
		}

		AnsiConsole.Write(table);
		if (dryRun)
		{
			WriteWarning(_text.Get("dry_run"));
			return 0;
		}

		var selected = AnsiConsole.Prompt(
			new MultiSelectionPrompt<InstallerCandidate>()
				.Title(_text.Get("packages_select"))
				.PageSize(15)
				.InstructionsText(_text.Get("cleanup_select_instructions"))
				.AddChoices(candidates));

		if (selected.Count == 0)
		{
			WriteWarning(_text.Get("cleanup_none_selected"));
			return 0;
		}

		var totalBytes = selected.Sum(candidate => candidate.Bytes);
		if (!AnsiConsole.Confirm(_text.Format("packages_confirm", selected.Count, FormatBytes(totalBytes)), defaultValue: false))
		{
			WriteWarning(_text.Get("cleanup_cancelled"));
			return 0;
		}

		var result = InstallerCandidateScanner.Delete(selected);
		WriteSuccess(_text.Format("packages_deleted", result.DeletedFiles, FormatBytes(result.DeletedBytes)));
		if (result.FailedFiles > 0)
		{
			WriteWarning(_text.Format("cleanup_failed", result.FailedFiles));
			PrintErrors(result.Errors);
		}

		return result.FailedFiles == 0 ? 0 : 1;
	}

	private void PrintCleanupPlan(IReadOnlyList<CleanupTarget> targets)
	{
		AnsiConsole.Write(new Rule(_text.Get("cleanup_plan")));
		if (targets.Count == 0)
		{
			AnsiConsole.WriteLine(_text.Get("cleanup_empty"));
			return;
		}

		var table = new Table { Border = TableBorder.Rounded };
		table.AddColumn(_text.Get("cleanup_columns_category"));
		table.AddColumn(_text.Get("cleanup_columns_item"));
		table.AddColumn(_text.Get("cleanup_columns_risk"));
		table.AddColumn(_text.Get("cleanup_columns_files"));
		table.AddColumn(_text.Get("cleanup_columns_size"));
		table.AddColumn(_text.Get("cleanup_columns_note"));
		foreach (var target in targets)
		{
			var note = target.Note.Get(_text.Language);
			if (target.RequiresAdministrator)
			{
				note += $" / {_text.Get("cleanup_note_admin")}";
			}

			if (target.OptIn)
			{
				note += $" / {_text.Get("cleanup_note_opt_in")}";
			}

			table.AddRow(
				new Text(target.Category.Get(_text.Language)),
				new Text(target.Name.Get(_text.Language)),
				new Text(GetRiskLabel(target.Risk)),
				new Text(target.Snapshot.FileCount.ToString()),
				new Text(FormatBytes(target.Snapshot.Bytes)),
				new Text(note));
		}

		AnsiConsole.Write(table);
	}

	private void ShowBanner()
	{
		AnsiConsole.Write(new Panel(new Text(_text.Get("welcome_body") + Environment.NewLine + Environment.NewLine + _text.Get("welcome_safety")))
		{
			Header = new PanelHeader(_text.Get("welcome_header")),
			Border = BoxBorder.Rounded
		});
		AnsiConsole.WriteLine();
	}

	private void ShowHelp()
	{
		AnsiConsole.Write(new Panel(new Text(_text.Get("help_body")))
		{
			Header = new PanelHeader(_text.Get("help_title")),
			Border = BoxBorder.Rounded
		});
		AnsiConsole.WriteLine();
		CliOptionsParser.WriteUsage(_text);
	}

	private void PrintErrors(IEnumerable<string> errors)
	{
		AnsiConsole.MarkupLine($"[grey]{Markup.Escape(_text.Get("cleanup_errors"))}[/]");
		foreach (var error in errors)
		{
			AnsiConsole.WriteLine($"  {error}");
		}
	}

	private void WriteSuccess(string message)
		=> AnsiConsole.MarkupLine($"[green]{Markup.Escape(message)}[/]");

	private void WriteWarning(string message)
		=> AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");

	private void WriteError(string message)
		=> AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");

	private static bool IsAdministrator()
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		try
		{
			using var identity = WindowsIdentity.GetCurrent();
			return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
		}
		catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static string FormatBytes(long bytes)
	{
		const double scale = 1024;
		if (bytes < scale)
		{
			return $"{bytes} B";
		}

		var units = new[] { "KB", "MB", "GB", "TB" };
		var unit = -1;
		double scaled = bytes;
		while (scaled >= scale && unit < units.Length - 1)
		{
			scaled /= scale;
			unit++;
		}

		return $"{scaled:0.##} {units[unit]}";
	}

	private string GetRiskLabel(CleanupRisk risk)
		=> risk == CleanupRisk.Moderate ? _text.Get("cleanup_risk_moderate") : _text.Get("cleanup_risk_safe");

	private sealed record MenuEntry(string Id, string Label)
	{
		public override string ToString() => Markup.Escape(Label);
	}

	private sealed record DuplicateChoice(string Path)
	{
		public override string ToString() => Markup.Escape(Path);
	}

	private sealed record CleanupChoice(CleanupTarget Target, AppLanguage Language)
	{
		public override string ToString()
			=> Markup.Escape($"{Target.Name.Get(Language)} ({(Target.Risk == CleanupRisk.Moderate ? (Language == AppLanguage.Chinese ? "需确认" : "review") : (Language == AppLanguage.Chinese ? "安全" : "safe"))}; {FormatBytes(Target.Snapshot.Bytes)}, {Target.Snapshot.FileCount} {(Language == AppLanguage.Chinese ? "个文件" : "files")})");
	}
}
