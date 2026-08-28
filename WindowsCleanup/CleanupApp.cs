using System.Security.Principal;
using Spectre.Console;
using Spectre.Console.Rendering;
using WindowsCleanup.Cleanup;
using WindowsCleanup.Localization;
using WindowsCleanup.Migration;
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
			CliCommand.Duplicates => await RunDuplicatesAsync().ConfigureAwait(false),
			CliCommand.Remnants => RunRemnants(options.DryRun),
			CliCommand.Packages => RunInstallerCandidates(options.DryRun),
			CliCommand.Migration => RunDataMigration(options.DryRun),
			CliCommand.LargeFiles => RunLargeFileAnalysis(options.DryRun),
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
				new MenuEntry("migration", _text.Get("menu_migration")),
				new MenuEntry("large-files", _text.Get("menu_large_files")),
				new MenuEntry("exit", _text.Get("menu_exit"))
			};

			var selected = AnsiConsole.Prompt(
				new SelectionPrompt<MenuEntry>()
					.Title(_text.Get("menu_title"))
					.PageSize(10)
					.AddChoices(choices)
					.AddCancelResult(choices.Single(choice => choice.Id == "exit")));

			switch (selected.Id)
			{
				case "clean":
					RunSafeCleanup(selectAll: false, dryRun: false);
					break;
				case "registry":
					RunRegistryCleanup(dryRun: false);
					break;
				case "duplicates":
					await RunDuplicatesAsync().ConfigureAwait(false);
					break;
				case "remnants":
					RunRemnants(dryRun: false);
					break;
				case "packages":
					RunInstallerCandidates(dryRun: false);
					break;
				case "migration":
					RunDataMigration(dryRun: false);
					break;
				case "large-files":
					RunLargeFileAnalysis(dryRun: false);
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
		try
		{
			using var cancellation = new CancellationTokenSource();
			using var escListener = new EscapeCancellationListener(cancellation);
			AnsiConsole.Status().Start(ScanStatus("scanning"), status =>
			{
				available = CleanupScanner.ScanTargets(
					targets,
					cancellation.Token,
					target => status.Status(WithScanCancelHint($"{_text.Get("scanning")} {target.Name.Get(_text.Language)}")));
			});
		}
		catch (OperationCanceledException)
		{
			WriteWarning(_text.Get("scan_cancelled"));
			return 0;
		}

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
			var choices = available
				.Select(target => new CleanupSelection(target, _text.Language))
				.ToArray();
			var selectedChoices = AnsiConsole.Prompt(
				new MultiSelectionPrompt<CleanupSelection>()
					.Title(_text.Get("cleanup_select"))
					.PageSize(15)
					.InstructionsText(_text.Get("cleanup_select_instructions"))
					.AddChoices(choices)
					.AddCancelResult(CleanupSelection.Cancel(_text.Language)));
			if (selectedChoices.Any(choice => choice.IsCancel))
			{
				WriteWarning(_text.Get("selection_cancelled"));
				return 0;
			}

			selected = selectedChoices
				.Where(choice => choice.Target is not null)
				.Select(choice => choice.Target!)
				.ToArray();
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

	private async Task<int> RunDuplicatesAsync()
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
		WriteWarning(ScanStatus("duplicates_scanning"));
		var finder = new DuplicateFinder();
		IReadOnlyList<DuplicateGroup> groups;
		try
		{
			using var cancellation = new CancellationTokenSource();
			using var escListener = new EscapeCancellationListener(cancellation);
			groups = await finder.FindAsync(roots, cancellation.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			WriteWarning(_text.Get("scan_cancelled"));
			return 0;
		}
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
			foreach (var duplicate in group.Files)
			{
				AnsiConsole.WriteLine($"  {duplicate}");
			}
		}

		WriteWarning(_text.Get("duplicates_list_only"));

		return 0;
	}

	private int RunRemnants(bool dryRun)
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
				CreatePathLink(candidate.Path),
				new Text(candidate.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
				new Text(FormatBytes(candidate.Bytes)));
		}

		AnsiConsole.Write(table);
		if (dryRun)
		{
			WriteWarning(_text.Get("dry_run"));
			return 0;
		}

		WriteWarning(_text.Get("remnants_manual"));
		var choices = candidates
			.Select(candidate => new RemnantSelection(candidate, _text.Language))
			.ToArray();
		var selectedChoices = AnsiConsole.Prompt(
			new MultiSelectionPrompt<RemnantSelection>()
				.Title(_text.Get("remnants_select"))
				.PageSize(15)
				.InstructionsText(_text.Get("cleanup_select_instructions"))
				.AddChoices(choices)
				.AddCancelResult(RemnantSelection.Cancel(_text.Language)));
		if (selectedChoices.Any(choice => choice.IsCancel))
		{
			WriteWarning(_text.Get("selection_cancelled"));
			return 0;
		}

		var selected = selectedChoices
			.Where(choice => choice.Candidate is not null)
			.Select(choice => choice.Candidate!)
			.ToArray();
		if (selected.Length == 0)
		{
			WriteWarning(_text.Get("cleanup_none_selected"));
			return 0;
		}

		var totalBytes = selected.Sum(candidate => candidate.Bytes);
		if (!AnsiConsole.Confirm(_text.Format("remnants_confirm", selected.Length, FormatBytes(totalBytes)), defaultValue: false))
		{
			WriteWarning(_text.Get("cleanup_cancelled"));
			return 0;
		}

		var result = StaleAppDataScanner.Delete(selected);
		WriteSuccess(_text.Format("remnants_deleted", result.DeletedFiles, FormatBytes(result.DeletedBytes)));
		if (result.SkippedCandidates > 0)
		{
			WriteWarning(_text.Format("remnants_skipped", result.SkippedCandidates));
		}
		if (result.FailedFiles > 0)
		{
			WriteWarning(_text.Format("cleanup_failed", result.FailedFiles));
			PrintErrors(result.Errors);
		}

		return result.FailedFiles == 0 ? 0 : 1;
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

		var choices = candidates
			.Select(candidate => new InstallerSelection(candidate, _text.Language))
			.ToArray();
		var selectedChoices = AnsiConsole.Prompt(
			new MultiSelectionPrompt<InstallerSelection>()
				.Title(_text.Get("packages_select"))
				.PageSize(15)
				.InstructionsText(_text.Get("cleanup_select_instructions"))
				.AddChoices(choices)
				.AddCancelResult(InstallerSelection.Cancel(_text.Language)));
		if (selectedChoices.Any(choice => choice.IsCancel))
		{
			WriteWarning(_text.Get("selection_cancelled"));
			return 0;
		}

		var selected = selectedChoices
			.Where(choice => choice.Candidate is not null)
			.Select(choice => choice.Candidate!)
			.ToArray();

		if (selected.Length == 0)
		{
			WriteWarning(_text.Get("cleanup_none_selected"));
			return 0;
		}

		var totalBytes = selected.Sum(candidate => candidate.Bytes);
		if (!AnsiConsole.Confirm(_text.Format("packages_confirm", selected.Length, FormatBytes(totalBytes)), defaultValue: false))
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

	private int RunDataMigration(bool dryRun)
	{
		AnsiConsole.Write(new Panel(new Text(_text.Get("migration_note")))
		{
			Header = new PanelHeader(_text.Get("migration_title")),
			Border = BoxBorder.Rounded
		});
		AnsiConsole.WriteLine();

		IReadOnlyList<MigrationTarget> targets = Array.Empty<MigrationTarget>();
		try
		{
			using var cancellation = new CancellationTokenSource();
			using var escListener = new EscapeCancellationListener(cancellation);
			AnsiConsole.Status().Start(ScanStatus("migration_scanning"), status =>
			{
				targets = DataMigrationCatalog.FindAvailable(
					path => status.Status(WithScanCancelHint(_text.Format("migration_scanning_path", Markup.Escape(path)))),
					cancellation.Token);
			});
		}
		catch (OperationCanceledException)
		{
			WriteWarning(_text.Get("scan_cancelled"));
			return 0;
		}
		if (targets.Count == 0)
		{
			WriteWarning(_text.Get("migration_none"));
			return 0;
		}

		var table = new Table { Border = TableBorder.Rounded };
		table.AddColumn(_text.Get("migration_columns_category"));
		table.AddColumn(_text.Get("migration_columns_name"));
		table.AddColumn(_text.Get("migration_columns_source"));
		table.AddColumn(_text.Get("migration_columns_size"));
		table.AddColumn(_text.Get("migration_columns_plan"));
		foreach (var target in targets)
		{
			table.AddRow(
				new Text(target.Category.Get(_text.Language)),
				new Text(target.Name.Get(_text.Language)),
				CreatePathLink(target.SourcePath),
				new Text(FormatBytes(target.Bytes)),
				new Text(target.Plan.Get(_text.Language)));
		}
		AnsiConsole.Write(table);
		AnsiConsole.WriteLine();

		if (dryRun)
		{
			WriteWarning(_text.Get("dry_run"));
			return 0;
		}

		var selectedChoices = AnsiConsole.Prompt(
			new MultiSelectionPrompt<MigrationChoice>()
				.Title(_text.Get("migration_select"))
				.PageSize(15)
				.InstructionsText(_text.Get("cleanup_select_instructions"))
				.AddChoices(targets
					.Select(target => new MigrationChoice(target, _text.Language)))
				.AddCancelResult(MigrationChoice.Cancel(_text.Language)));
		if (selectedChoices.Any(choice => choice.IsCancel))
		{
			WriteWarning(_text.Get("selection_cancelled"));
			return 0;
		}

		var selected = selectedChoices
			.Where(choice => choice.Target is not null)
			.Select(choice => choice.Target!)
			.ToArray();
		if (selected.Length == 0)
		{
			WriteWarning(_text.Get("cleanup_none_selected"));
			return 0;
		}

		var drives = DataMigrationService.GetDestinationDrives();
		if (drives.Count == 0)
		{
			WriteWarning(_text.Get("migration_no_drives"));
			return 1;
		}

		var driveChoice = AnsiConsole.Prompt(
			new SelectionPrompt<MigrationDriveChoice>()
				.Title(_text.Get("migration_drive_select"))
				.PageSize(10)
				.AddChoices(drives
					.Select(drive => new MigrationDriveChoice(drive, _text.Language)))
				.AddCancelResult(MigrationDriveChoice.Cancel(_text.Language)));
		if (driveChoice.IsCancel)
		{
			WriteWarning(_text.Get("selection_cancelled"));
			return 0;
		}

		var drive = driveChoice.Drive!;
		IReadOnlyList<MigrationPlanItem> plan = Array.Empty<MigrationPlanItem>();
		try
		{
			using var cancellation = new CancellationTokenSource();
			using var escListener = new EscapeCancellationListener(cancellation);
			AnsiConsole.Status().Start(ScanStatus("migration_rechecking"), status =>
			{
				plan = DataMigrationService.BuildPlan(
					selected,
					drive.RootPath,
					path => status.Status(WithScanCancelHint(_text.Format("migration_rechecking_path", Markup.Escape(path)))),
					cancellation.Token);
			});
		}
		catch (OperationCanceledException)
		{
			WriteWarning(_text.Get("scan_cancelled"));
			return 0;
		}
		if (plan.Count == 0)
		{
			WriteWarning(_text.Get("migration_no_valid_targets"));
			return 1;
		}

		PrintMigrationPlan(plan);
		if (!AnsiConsole.Confirm(_text.Format("migration_confirm", plan.Count, FormatBytes(plan.Sum(item => item.Bytes))), defaultValue: false))
		{
			WriteWarning(_text.Get("cleanup_cancelled"));
			return 0;
		}

		MigrationExecutionResult result = new(0, 0, 0, Array.Empty<string>());
		AnsiConsole.Status().Start(_text.Get("migration_running"), status =>
		{
			result = DataMigrationService.Execute(
				plan,
				_text.Language,
				path => status.Status(_text.Format("migration_running_path", Markup.Escape(path))));
		});
		WriteSuccess(_text.Format("migration_done", result.MigratedTargets, FormatBytes(result.MigratedBytes)));
		if (result.FailedTargets > 0)
		{
			WriteWarning(_text.Format("migration_failed", result.FailedTargets));
			PrintErrors(result.Errors);
		}

		return result.FailedTargets == 0 ? 0 : 1;
	}

	private int RunLargeFileAnalysis(bool dryRun)
	{
		AnsiConsole.Write(new Panel(new Text(_text.Get("large_files_note")))
		{
			Header = new PanelHeader(_text.Get("large_files_title")),
			Border = BoxBorder.Rounded
		});
		AnsiConsole.WriteLine();

		IReadOnlyList<LargeFileCandidate> candidates = Array.Empty<LargeFileCandidate>();
		LargeFileScanProgress? scanProgress = null;
		try
		{
			using var cancellation = new CancellationTokenSource();
			using var escListener = new EscapeCancellationListener(cancellation);
			AnsiConsole.Status().Start(ScanStatus("large_files_scanning"), status =>
			{
				candidates = LargeFileScanner.Find(progress =>
				{
					scanProgress = progress;
					status.Status(WithScanCancelHint(_text.Format(
						"large_files_scanning_status",
						Markup.Escape(progress.CurrentPath),
						progress.ScannedDirectories,
						progress.ScannedFiles,
						progress.CandidateCount,
						progress.SkippedEntries)));
				}, cancellation.Token);
			});
		}
		catch (OperationCanceledException)
		{
			WriteWarning(_text.Get("scan_cancelled"));
			return 0;
		}

		if (scanProgress?.SkippedEntries > 0)
		{
			WriteWarning(_text.Format("large_files_scan_skipped", scanProgress.SkippedEntries));
		}

		if (candidates.Count == 0)
		{
			WriteSuccess(_text.Get("large_files_none"));
			return 0;
		}

		var table = new Table { Border = TableBorder.Rounded };
		table.AddColumn(_text.Get("large_files_columns_path"));
		table.AddColumn(_text.Get("large_files_columns_size"));
		foreach (var candidate in candidates)
		{
			table.AddRow(CreatePathLink(candidate.Path), new Text(FormatBytes(candidate.Bytes)));
		}

		AnsiConsole.Write(table);
		if (scanProgress?.CandidateCount > candidates.Count)
		{
			WriteWarning(_text.Format("large_files_limit", LargeFileScanner.MaximumDisplayedFiles));
		}

		if (dryRun)
		{
			WriteWarning(_text.Get("dry_run"));
			return 0;
		}

		var choices = candidates
			.Select(candidate => new LargeFileSelection(candidate, _text.Language))
			.ToArray();
		var selectedChoices = AnsiConsole.Prompt(
			new MultiSelectionPrompt<LargeFileSelection>()
				.Title(_text.Get("large_files_select"))
				.PageSize(15)
				.InstructionsText(_text.Get("cleanup_select_instructions"))
				.AddChoices(choices)
				.AddCancelResult(LargeFileSelection.Cancel(_text.Language)));
		if (selectedChoices.Any(choice => choice.IsCancel))
		{
			WriteWarning(_text.Get("selection_cancelled"));
			return 0;
		}

		var selected = selectedChoices
			.Where(choice => choice.Candidate is not null)
			.Select(choice => choice.Candidate!)
			.ToArray();
		if (selected.Length == 0)
		{
			WriteWarning(_text.Get("cleanup_none_selected"));
			return 0;
		}

		var totalBytes = selected.Sum(candidate => candidate.Bytes);
		WriteWarning(_text.Get("large_files_risk_warning"));
		if (!AnsiConsole.Confirm(
			_text.Format("large_files_confirm", selected.Length, FormatBytes(totalBytes)),
			defaultValue: false))
		{
			WriteWarning(_text.Get("cleanup_cancelled"));
			return 0;
		}

		var result = LargeFileScanner.Delete(selected);
		WriteSuccess(_text.Format("large_files_deleted", result.DeletedFiles, FormatBytes(result.DeletedBytes)));
		if (result.SkippedFiles > 0)
		{
			WriteWarning(_text.Format("large_files_delete_skipped", result.SkippedFiles));
		}
		if (result.FailedFiles > 0)
		{
			WriteWarning(_text.Format("large_files_delete_failed", result.FailedFiles));
			PrintErrors(result.Errors);
		}

		return result.FailedFiles == 0 ? 0 : 1;
	}

	private void PrintMigrationPlan(IReadOnlyList<MigrationPlanItem> plan)
	{
		var table = new Table { Border = TableBorder.Rounded };
		table.AddColumn(_text.Get("migration_columns_name"));
		table.AddColumn(_text.Get("migration_columns_source"));
		table.AddColumn(_text.Get("migration_columns_destination"));
		table.AddColumn(_text.Get("migration_columns_config"));
		table.AddColumn(_text.Get("migration_columns_size"));
		foreach (var item in plan)
		{
			table.AddRow(
				new Text(item.Target.Name.Get(_text.Language)),
				CreatePathLink(item.SourcePath),
				CreatePathLink(item.DestinationPath),
				new Text(item.Target.ConfigurationPlan.Get(_text.Language)),
				new Text(FormatBytes(item.Bytes)));
		}
		AnsiConsole.Write(table);
		WriteWarning(_text.Get("migration_plan_warning"));
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

	private static IRenderable CreatePathLink(string path)
	{
		var display = Markup.Escape(path);
		try
		{
			var uri = new Uri(Path.GetFullPath(path), UriKind.Absolute).AbsoluteUri;
			return new Markup($"[link={Markup.Escape(uri)}]{display}[/]");
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UriFormatException)
		{
			return new Text(path);
		}
	}

	private string GetRiskLabel(CleanupRisk risk)
		=> risk == CleanupRisk.Moderate ? _text.Get("cleanup_risk_moderate") : _text.Get("cleanup_risk_safe");

	private string ScanStatus(string key)
		=> WithScanCancelHint(_text.Get(key));

	private string WithScanCancelHint(string message)
		=> $"{message} {_text.Get("scan_cancel_hint")}";

	private sealed class EscapeCancellationListener : IDisposable
	{
		private readonly CancellationTokenSource _cancellation;
		private readonly CancellationTokenSource _stop = new();
		private readonly Task _listener;

		public EscapeCancellationListener(CancellationTokenSource cancellation)
		{
			_cancellation = cancellation;
			_listener = Task.Run(ListenAsync);
		}

		private async Task ListenAsync()
		{
			if (System.Console.IsInputRedirected)
			{
				return;
			}

			while (!_stop.IsCancellationRequested && !_cancellation.IsCancellationRequested)
			{
				try
				{
					if (System.Console.KeyAvailable)
					{
						var key = System.Console.ReadKey(intercept: true);
						if (key.Key == ConsoleKey.Escape)
						{
							_cancellation.Cancel();
							return;
						}
					}
				}
				catch (Exception exception) when (exception is InvalidOperationException or IOException or NotSupportedException)
				{
					return;
				}

				try
				{
					await Task.Delay(50, _stop.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (_stop.IsCancellationRequested)
				{
					return;
				}
			}
		}

		public void Dispose()
		{
			_stop.Cancel();
			try
			{
				_listener.GetAwaiter().GetResult();
			}
			catch (Exception exception) when (exception is InvalidOperationException or IOException or NotSupportedException or ObjectDisposedException)
			{
				// Console input can disappear while a scan is finishing.
			}
			_stop.Dispose();
		}
	}

	private sealed record MenuEntry(string Id, string Label)
	{
		public override string ToString() => Markup.Escape(Label);
	}

	private static string GetCancelLabel(AppLanguage language)
		=> language == AppLanguage.Chinese ? "取消" : "Cancel";

	private sealed record CleanupSelection(CleanupTarget? Target, AppLanguage Language, bool IsCancel = false)
	{
		public static CleanupSelection Cancel(AppLanguage language)
			=> new(null, language, IsCancel: true);

		public override string ToString()
		{
			if (IsCancel)
			{
				return Markup.Escape(GetCancelLabel(Language));
			}

			var target = Target!;
			return Markup.Escape($"{target.Name.Get(Language)} ({(target.Risk == CleanupRisk.Moderate ? (Language == AppLanguage.Chinese ? "需确认" : "review") : (Language == AppLanguage.Chinese ? "安全" : "safe"))}; {FormatBytes(target.Snapshot.Bytes)}, {target.Snapshot.FileCount} {(Language == AppLanguage.Chinese ? "个文件" : "files")})");
		}
	}

	private sealed record RemnantSelection(AppDataCandidate? Candidate, AppLanguage Language, bool IsCancel = false)
	{
		public static RemnantSelection Cancel(AppLanguage language)
			=> new(null, language, IsCancel: true);

		public override string ToString()
			=> IsCancel
				? Markup.Escape(GetCancelLabel(Language))
				: Markup.Escape($"{Candidate!.Path} ({FormatBytes(Candidate.Bytes)})");
	}

	private sealed record InstallerSelection(InstallerCandidate? Candidate, AppLanguage Language, bool IsCancel = false)
	{
		public static InstallerSelection Cancel(AppLanguage language)
			=> new(null, language, IsCancel: true);

		public override string ToString()
			=> IsCancel
				? Markup.Escape(GetCancelLabel(Language))
				: Markup.Escape($"{Candidate!.Path} ({FormatBytes(Candidate.Bytes)})");
	}

	private sealed record MigrationChoice(MigrationTarget? Target, AppLanguage Language, bool IsCancel = false)
	{
		public static MigrationChoice Cancel(AppLanguage language)
			=> new(null, language, IsCancel: true);

		public override string ToString()
			=> IsCancel
				? Markup.Escape(GetCancelLabel(Language))
				: Markup.Escape($"{Target!.Name.Get(Language)} - {Target.SourcePath} ({FormatBytes(Target.Bytes)})");
	}

	private sealed record MigrationDriveChoice(MigrationDrive? Drive, AppLanguage Language, bool IsCancel = false)
	{
		public static MigrationDriveChoice Cancel(AppLanguage language)
			=> new(null, language, IsCancel: true);

		public override string ToString()
			=> IsCancel
				? Markup.Escape(GetCancelLabel(Language))
				: Markup.Escape(Drive!.RootPath);
	}

	private sealed record LargeFileSelection(LargeFileCandidate? Candidate, AppLanguage Language, bool IsCancel = false)
	{
		public static LargeFileSelection Cancel(AppLanguage language)
			=> new(null, language, IsCancel: true);

		public override string ToString()
			=> IsCancel
				? Markup.Escape(GetCancelLabel(Language))
				: Markup.Escape($"{Candidate!.Path} ({FormatBytes(Candidate.Bytes)})");
	}
}
