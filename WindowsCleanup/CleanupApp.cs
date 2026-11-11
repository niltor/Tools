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
			CliCommand.Developer => RunSafeCleanup(false, options.DryRun, true, options.ProjectRoots),
			CliCommand.DeveloperPackages => RunSafeCleanup(false, options.DryRun, packageCaches: true),
			CliCommand.Registry => RunRegistryCleanup(options.DryRun),
			CliCommand.Duplicates => await RunDuplicatesAsync().ConfigureAwait(false),
			CliCommand.Remnants => RunRemnants(options.DryRun),
			CliCommand.Packages => RunInstallerCandidates(options.DryRun),
			CliCommand.Migration => RunDataMigration(options.DryRun),
			CliCommand.LargeFiles => RunLargeFileAnalysis(options.DryRun),
			CliCommand.Containers => await RunContainerCleanupAsync(options.DryRun),
			CliCommand.ContainerReview => await RunContainerCleanupAsync(options.DryRun, review: true),
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
				new MenuEntry("developer", _text.Get("menu_developer")),
				new MenuEntry("developer-packages", _text.Get("menu_developer_packages")),
				new MenuEntry("registry", _text.Get("menu_registry")),
				new MenuEntry("duplicates", _text.Get("menu_duplicates")),
				new MenuEntry("remnants", _text.Get("menu_remnants")),
				new MenuEntry("packages", _text.Get("menu_packages")),
				new MenuEntry("migration", _text.Get("menu_migration")),
				new MenuEntry("large-files", _text.Get("menu_large_files")),
				new MenuEntry("containers", _text.Get("menu_containers")),
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
				case "developer":
					RunSafeCleanup(false, false, true);
					break;
				case "developer-packages":
					RunSafeCleanup(false, false, packageCaches: true);
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
				case "containers":
					var review = AnsiConsole.Confirm(_text.Get("container_include_review"), defaultValue: false);
					await RunContainerCleanupAsync(dryRun: false, review: review);
					break;
				case "exit":
					WriteSuccess(_text.Get("exit_message"));
					return 0;
			}

			AnsiConsole.WriteLine();
		}
	}

	private async Task<int> RunContainerCleanupAsync(bool dryRun, bool review = false)
	{
		var chinese = _text.Language == AppLanguage.Chinese;
		if (review)
			WriteWarning(chinese ? "未引用只表示当前没有容器关联。删除有标签镜像可能需要重新下载或构建；卷可能保存数据库等持久数据。"
				: "Unreferenced means no current container uses the resource. Tagged images may need pulling or rebuilding; volumes may hold persistent data.");
		var found = false;
		var failed = false;
		foreach (var command in new[] { "docker", "podman" })
		{
			var engine = new ContainerCleanup(command);
			string[] connection;
			IReadOnlyList<ContainerCandidate> items;
			try
			{
				connection = await engine.GetLocalConnectionAsync();
				found = true;
				items = (await engine.ScanAsync(connection, review: review))
					.OrderByDescending(item => item.Bytes).ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray();
			}
			catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 2 or 3) { continue; }
			catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
			{
				found = true;
				failed = true;
				WriteWarning(command + ": " + ex.Message);
				continue;
			}

			AnsiConsole.Write(new Rule(command));
			if (items.Count == 0)
			{
				AnsiConsole.WriteLine(review
					? (chinese ? "没有待审核的有标签镜像或卷。" : "No tagged images or volumes to review.")
					: (chinese ? "没有发现可清理的无标签镜像。" : "No dangling images eligible for cleanup."));
				continue;
			}
			var options = items.Where(item => !item.IsVolume).Select(item =>
			{
				var kind = item.Untagged ? (chinese ? "无标签镜像" : "Untagged image") : (chinese ? "未引用镜像" : "Unreferenced image");
				var space = item.Bytes is long bytes ? FormatBytes(bytes) : (chinese ? "未知" : "Unknown");
				return new ContainerSelection([item], $"{space} · {kind}: {item.Label} [{item.Id[..12]}] ({item.Size})", item.Bytes);
			}).ToList();
			var volumes = items.Where(item => item.IsVolume).ToArray();
			if (volumes.Length > 0)
			{
				var unknownVolumes = volumes.Count(item => !item.Bytes.HasValue);
				long? volumeBytes = unknownVolumes == volumes.Length ? null : volumes.Sum(item => item.Bytes.GetValueOrDefault());
				var space = volumeBytes is long bytes ? FormatBytes(bytes) : (chinese ? "未知" : "Unknown");
				var label = chinese ? $"全部未引用卷 · {volumes.Length} 个 · {space}" : $"All unreferenced volumes · {volumes.Length} · {space}";
				if (unknownVolumes > 0) label += chinese ? $"（{unknownVolumes} 个大小未知）" : $" ({unknownVolumes} unknown sizes)";
				options.Add(new ContainerSelection(volumes, label, volumeBytes));
			}
			var ordered = options.OrderByDescending(option => option.Bytes).ToArray();
			if (dryRun)
			{
				foreach (var option in ordered) AnsiConsole.WriteLine(option.Label);
				continue;
			}

			var cancel = new ContainerSelection([], GetCancelLabel(_text.Language), null);
			var selected = PromptMultiSelection(ordered, cancel, _text.Get("cleanup_select"),
				_text.Get("cleanup_select_instructions"));
			if (selected.Any(choice => choice.Items.Length == 0) || selected.Count == 0) continue;
			var selectedItems = selected.SelectMany(choice => choice.Items)
				.DistinctBy(item => (item.Id, item.IsVolume)).ToArray();
			var known = selectedItems.Where(item => item.Bytes.HasValue).ToArray();
			var unknown = selectedItems.Length - known.Length;
			var estimated = known.Length == 0 ? (chinese ? "未知" : "Unknown") : FormatBytes(known.Sum(item => item.Bytes!.Value));
			AnsiConsole.WriteLine(chinese
				? $"预计释放合计：{estimated}（镜像只计独占层，卷按占用计；{unknown} 项大小未知）。"
				: $"Estimated total reclaim: {estimated} (exclusive image layers and volume usage; {unknown} unknown).");
			var selectedVolumeCount = selectedItems.Count(item => item.IsVolume);
			var confirm = selectedVolumeCount > 0
				? (chinese ? $"删除选中的容器资源？其中 {selectedVolumeCount} 个卷及其数据将永久删除。"
					: $"Delete selected resources? This will permanently remove {selectedVolumeCount} volumes and their data.")
				: (chinese ? "删除选中的镜像？" : "Delete the selected images?");
			if (!AnsiConsole.Confirm(confirm, defaultValue: false)) continue;
			try
			{
				var current = (await engine.ScanAsync(connection, measureSpace: false, review: review)).Select(item => (item.Id, item.IsVolume, item.Label)).ToHashSet();
				var before = await engine.MeasureStorageAsync(connection);
				var removed = 0;
				var imagesRemoved = false;
				var volumesRemoved = false;
				foreach (var item in selectedItems)
			{
					if (!current.Contains((item.Id, item.IsVolume, item.Label)))
					{
						WriteWarning(item.Id + (chinese ? "：已变化，跳过。" : ": changed, skipped."));
						continue;
					}
					try
					{
						await engine.DeleteAsync(connection, item);
						removed++;
						if (item.IsVolume) volumesRemoved = true;
						else imagesRemoved = true;
					}
					catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
					{
						failed = true;
						WriteWarning(item.Id + ": " + ex.Message);
					}
				}
				WriteSuccess(chinese ? $"已删除 {removed} 个容器资源。" : $"Removed {removed} container resources.");
				var after = await engine.MeasureStorageAsync(connection);
				long? imageDelta = imagesRemoved ? before.Images - after.Images : 0;
				long? volumeDelta = volumesRemoved ? before.Volumes - after.Volumes : 0;
				if (imageDelta + volumeDelta is long delta)
				{
					var amount = FormatBytes(Math.Abs(delta));
					AnsiConsole.WriteLine(delta >= 0
						? (chinese ? $"引擎实际占用净减少约 {amount}。" : $"Engine storage usage decreased by about {amount}.")
						: (chinese ? $"引擎实际占用净增加约 {amount}。" : $"Engine storage usage increased by about {amount}."));
				}
				else WriteWarning(chinese ? "引擎未提供完整统计，无法测量实际释放空间。" : "The engine did not provide enough data to measure reclaimed space.");
				AnsiConsole.WriteLine(chinese ? "此数值来自引擎清理前后的占用差，可能受并发活动影响；不等于 Windows 磁盘可用空间变化。"
					: "This is the measured engine usage change, which may include concurrent activity; Windows free space can differ.");
			}
			catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
			{
				failed = true;
				WriteWarning(command + ": " + ex.Message);
			}
		}
		if (!found) WriteWarning(chinese ? "未找到 Docker 或 Podman 命令。" : "Docker and Podman commands were not found.");
		if (dryRun) WriteWarning(_text.Get("dry_run"));
		return failed ? 1 : 0;
	}

	private sealed record ContainerSelection(ContainerCandidate[] Items, string Label, long? Bytes)
	{
		public override string ToString() => Markup.Escape(Label);
	}

	private int RunSafeCleanup(bool selectAll, bool dryRun, bool developer = false, IReadOnlyList<string>? projectRoots = null, bool packageCaches = false)
	{
		IReadOnlyList<CleanupTarget> available = Array.Empty<CleanupTarget>();
		try
		{
			using var cancellation = new CancellationTokenSource();
			using var escListener = new EscapeCancellationListener(cancellation);
			AnsiConsole.Status().Start(ScanStatus("scanning"), status =>
			{
				var targets = packageCaches ? DeveloperCacheCatalog.Build(DeveloperCacheKind.Packages) : developer
					? DeveloperCacheCatalog.Build(DeveloperCacheKind.ProjectBuild).Concat(ProjectCacheDiscovery.Discover(
						projectRoots is { Count: > 0 } ? projectRoots : ProjectCacheDiscovery.DefaultRoots(), cancellation.Token)).ToArray()
					: CleanupCatalog.Build();
				available = CleanupScanner.ScanTargets(
					targets,
					cancellation.Token,
					target => status.Status(WithScanCancelHint($"{_text.Get("scanning")} {target.Name.Get(_text.Language)}")))
					.OrderByDescending(target => target.Snapshot.Bytes)
					.ThenBy(target => target.Name.Get(_text.Language), StringComparer.CurrentCultureIgnoreCase).ToArray();
			});
		}
		catch (OperationCanceledException)
		{
			WriteWarning(_text.Get("scan_cancelled"));
			return 0;
		}

		AnsiConsole.MarkupLine($"[green]{Markup.Escape(_text.Get("scan_complete"))}[/]");
		if (dryRun || selectAll || available.Count == 0) PrintCleanupPlan(available);
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
			var selectedChoices = PromptMultiSelection(choices, CleanupSelection.Cancel(_text.Language),
				_text.Get("cleanup_select"), _text.Get("cleanup_select_instructions"));
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
		var confirmation = selected.Any(target => !target.Snapshot.IsMeasured)
			? _text.Format("cleanup_confirm_unmeasured", selected.Count)
			: _text.Format("cleanup_confirm", selected.Count, FormatBytes(totalBytes));
		if (!AnsiConsole.Confirm(confirmation, defaultValue: false))
		{
			WriteWarning(_text.Get("cleanup_cancelled"));
			return 0;
		}

		var totalDeletedFiles = 0;
		long totalDeletedBytes = 0;
		var freeSpaceBefore = GetFreeSpaceSnapshot(selected.SelectMany(target => target.Locations).Select(location => location.Path));
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
		ReportObservedRelease(freeSpaceBefore);
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
		AnsiConsole.WriteLine(_text.Get("registry_release_unknown"));
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
		var displayedGroups = groups
			.OrderByDescending(group => group.Size)
			.ThenBy(group => group.Files.FirstOrDefault(), StringComparer.OrdinalIgnoreCase)
			.Take(maximumDisplayedGroups).ToArray();
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

		var candidates = StaleAppDataScanner.Find()
			.OrderByDescending(candidate => candidate.Bytes)
			.ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		if (candidates.Length == 0)
		{
			WriteSuccess(_text.Get("remnants_none"));
			return 0;
		}

		if (dryRun)
		{
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
			WriteWarning(_text.Get("dry_run"));
			return 0;
		}

		WriteWarning(_text.Get("remnants_manual"));
		var choices = candidates
			.Select(candidate => new RemnantSelection(candidate, _text.Language))
			.ToArray();
		var selectedChoices = PromptMultiSelection(choices, RemnantSelection.Cancel(_text.Language),
			_text.Get("remnants_select"), _text.Get("cleanup_select_instructions"));
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

		var freeSpaceBefore = GetFreeSpaceSnapshot(selected.Select(candidate => candidate.Path));
		var result = StaleAppDataScanner.Delete(selected);
		WriteSuccess(_text.Format("remnants_deleted", result.DeletedFiles, FormatBytes(result.DeletedBytes)));
		ReportObservedRelease(freeSpaceBefore);
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

		var candidates = InstallerCandidateScanner.Find()
			.OrderByDescending(candidate => candidate.Bytes)
			.ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		if (candidates.Length == 0)
		{
			WriteSuccess(_text.Get("packages_none"));
			return 0;
		}

		if (dryRun)
		{
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
			WriteWarning(_text.Get("dry_run"));
			return 0;
		}

		var choices = candidates
			.Select(candidate => new InstallerSelection(candidate, _text.Language))
			.ToArray();
		var selectedChoices = PromptMultiSelection(choices, InstallerSelection.Cancel(_text.Language),
			_text.Get("packages_select"), _text.Get("cleanup_select_instructions"));
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

		var freeSpaceBefore = GetFreeSpaceSnapshot(selected.Select(candidate => candidate.Path));
		var result = InstallerCandidateScanner.Delete(selected);
		WriteSuccess(_text.Format("packages_deleted", result.DeletedFiles, FormatBytes(result.DeletedBytes)));
		ReportObservedRelease(freeSpaceBefore);
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
		targets = targets
			.OrderByDescending(target => target.Bytes)
			.ThenBy(target => target.SourcePath, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		if (dryRun)
		{
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
			WriteWarning(_text.Get("dry_run"));
			return 0;
		}

		var migrationChoices = targets.Select(target => new MigrationChoice(target, _text.Language)).ToArray();
		var selectedChoices = PromptMultiSelection(migrationChoices, MigrationChoice.Cancel(_text.Language),
			_text.Get("migration_select"), _text.Get("cleanup_select_instructions"));
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
		plan = plan
			.OrderByDescending(item => item.Bytes)
			.ThenBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		PrintMigrationPlan(plan);
		if (!AnsiConsole.Confirm(_text.Format("migration_confirm", plan.Count, FormatBytes(plan.Sum(item => item.Bytes))), defaultValue: false))
		{
			WriteWarning(_text.Get("cleanup_cancelled"));
			return 0;
		}

		var freeSpaceBefore = GetFreeSpaceSnapshot(plan.SelectMany(item => new[] { item.SourcePath, item.DestinationPath }));
		MigrationExecutionResult result = new(0, 0, 0, Array.Empty<string>());
		AnsiConsole.Status().Start(_text.Get("migration_running"), status =>
		{
			result = DataMigrationService.Execute(
				plan,
				_text.Language,
				path => status.Status(_text.Format("migration_running_path", Markup.Escape(path))));
		});
		WriteSuccess(_text.Format("migration_done", result.MigratedTargets, FormatBytes(result.MigratedBytes)));
		ReportObservedRelease(freeSpaceBefore);
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

		candidates = candidates
			.OrderByDescending(candidate => candidate.Bytes)
			.ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		if (candidates.Count == 0)
		{
			WriteSuccess(_text.Get("large_files_none"));
			return 0;
		}

		if (dryRun)
		{
			var table = new Table { Border = TableBorder.Rounded };
			table.AddColumn(_text.Get("large_files_columns_path"));
			table.AddColumn(_text.Get("large_files_columns_size"));
			foreach (var candidate in candidates)
			{
				table.AddRow(CreatePathLink(candidate.Path), new Text(FormatBytes(candidate.Bytes)));
			}

			AnsiConsole.Write(table);
		}
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
		var selectedChoices = PromptMultiSelection(choices, LargeFileSelection.Cancel(_text.Language),
			_text.Get("large_files_select"), _text.Get("cleanup_select_instructions"));
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

		var freeSpaceBefore = GetFreeSpaceSnapshot(selected.Select(candidate => candidate.Path));
		var result = LargeFileScanner.Delete(selected);
		WriteSuccess(_text.Format("large_files_deleted", result.DeletedFiles, FormatBytes(result.DeletedBytes)));
		ReportObservedRelease(freeSpaceBefore);
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
		foreach (var item in plan.OrderByDescending(item => item.Bytes).ThenBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase))
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
				new Text(target.Snapshot.IsMeasured ? target.Snapshot.FileCount.ToString() : _text.Get("cleanup_not_measured")),
				new Text(target.Snapshot.IsMeasured ? FormatBytes(target.Snapshot.Bytes) : _text.Get("cleanup_not_measured")),
				new Text(note));
		}

		AnsiConsole.Write(table);
		if (targets.Any(target => !target.Snapshot.IsMeasured)) WriteWarning(_text.Get("cleanup_directory_preview"));
		foreach (var group in targets.Where(target => target.DeveloperKind != DeveloperCacheKind.None).GroupBy(target => target.DeveloperKind))
		{
			AnsiConsole.WriteLine(_text.Format("developer_group_summary", group.First().Category.Get(_text.Language),
				group.Count(), group.Sum(target => target.Snapshot.LocationsWithFiles),
				group.All(target => target.Snapshot.IsMeasured) ? FormatBytes(group.Sum(target => target.Snapshot.Bytes)) : _text.Get("cleanup_not_measured")));
		}
		foreach (var target in targets.Where(target => target.DeveloperKind != DeveloperCacheKind.None &&
			!target.Id.StartsWith("dev-project-", StringComparison.Ordinal)))
		{
			AnsiConsole.WriteLine(target.Name.Get(_text.Language));
			var paths = target.Snapshot.IsMeasured ? target.MeasuredPaths
				: target.Locations.Where(location => Directory.Exists(location.Path)).Select(location => location.Path);
			foreach (var path in paths) AnsiConsole.WriteLine("  " + path);
		}
	}

	private IReadOnlyList<T> PromptMultiSelection<T>(IReadOnlyList<T> choices, T cancel, string title, string instructions)
		where T : notnull
	{
		if (choices.Count <= 5)
		{
			return AnsiConsole.Prompt(new MultiSelectionPrompt<T>()
				.Title(title)
				.PageSize(15)
				.InstructionsText(instructions)
				.AddChoices(choices)
				.AddCancelResult(cancel));
		}
		return PromptLargeMultiSelection(choices, cancel, title, instructions);
	}

	private IReadOnlyList<T> PromptLargeMultiSelection<T>(IReadOnlyList<T> choices, T cancel, string title, string instructions)
		where T : notnull
	{
		var chinese = _text.Language == AppLanguage.Chinese;
		if (System.Console.IsInputRedirected || System.Console.IsOutputRedirected || !AnsiConsole.Profile.Capabilities.Ansi)
		{
			WriteWarning(chinese ? "当前终端不支持交互式按键，已取消选择。" : "This terminal does not support interactive key input; selection cancelled.");
			return new[] { cancel };
		}

		var selected = new bool[choices.Count];
		var current = 0;
		var pageSize = Math.Clamp(System.Console.WindowHeight - 9, 5, 15);
		IRenderable Render()
		{
			var first = current / pageSize * pageSize;
			var last = Math.Min(choices.Count, first + pageSize);
			var rows = new List<IRenderable>
			{
				new Rule(title),
				new Markup(instructions),
				new Text(chinese ? "A 全选/取消全选；←→ 翻页。" : "A select/deselect all; Left/Right page.")
			};
			for (var index = first; index < last; index++)
			{
				var prefix = (index == current ? "> " : "  ") + (selected[index] ? "[x] " : "[ ] ");
				rows.Add(new Markup(Markup.Escape(prefix) + choices[index]));
			}
			var count = selected.Count(value => value);
			rows.Add(new Text(chinese ? $"第 {first + 1}–{last} 项，共 {choices.Count} 项；已选 {count} 项。"
				: $"Items {first + 1}–{last} of {choices.Count}; selected {count}."));
			return new Rows(rows);
		}

		try
		{
			return AnsiConsole.Live(Render()).AutoClear(true).Start<IReadOnlyList<T>>(context =>
			{
				context.Refresh();
				while (true)
				{
					var key = AnsiConsole.Console.Input.ReadKey(intercept: true);
					if (key is null) return new[] { cancel };
					switch (key.Value.Key)
					{
						case ConsoleKey.Escape: return new[] { cancel };
						case ConsoleKey.Enter: return choices.Where((_, index) => selected[index]).ToArray();
						case ConsoleKey.Spacebar: selected[current] = !selected[current]; break;
						case ConsoleKey.A when (key.Value.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control)) == 0:
							Array.Fill(selected, selected.Any(value => !value));
							break;
						case ConsoleKey.UpArrow: current = (current + choices.Count - 1) % choices.Count; break;
						case ConsoleKey.DownArrow: current = (current + 1) % choices.Count; break;
						case ConsoleKey.LeftArrow or ConsoleKey.PageUp: current = Math.Max(0, current - pageSize); break;
						case ConsoleKey.RightArrow or ConsoleKey.PageDown: current = Math.Min(choices.Count - 1, current + pageSize); break;
						case ConsoleKey.Home: current = 0; break;
						case ConsoleKey.End: current = choices.Count - 1; break;
						default: continue;
					}
					context.UpdateTarget(Render());
					context.Refresh();
				}
			});
		}
		catch (Exception ex) when (ex is InvalidOperationException or IOException)
		{
			WriteWarning(chinese ? "当前终端不支持交互式按键，已取消选择。" : "This terminal does not support interactive key input; selection cancelled.");
			return new[] { cancel };
		}
	}
	private void ShowBanner()
	{
		AnsiConsole.Write(new Panel(new Text(_text.Get("welcome_body")))
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

	private void ReportObservedRelease(IReadOnlyDictionary<string, long?> freeSpaceBefore)
	{
		var freeSpaceAfter = GetFreeSpaceSnapshot(freeSpaceBefore.Keys);
		foreach (var (root, before) in freeSpaceBefore)
		{
			var delta = freeSpaceAfter.GetValueOrDefault(root) - before;
			var value = delta is long bytes ? (bytes < 0 ? "−" : "+") + FormatBytes(Math.Abs(bytes)) : _text.Get("cleanup_space_unknown");
			AnsiConsole.WriteLine(_text.Format("cleanup_actual_release", root, value));
		}
	}

	private static IReadOnlyDictionary<string, long?> GetFreeSpaceSnapshot(IEnumerable<string> paths)
	{
		var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var path in paths)
		{
			try
			{
				var fullPath = Path.GetFullPath(path);
				var root = Path.GetPathRoot(fullPath);
				if (!string.IsNullOrWhiteSpace(root)) roots.Add(root);
			}
			catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException) { }
		}
		var result = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
		foreach (var root in roots)
		{
			try { result[root] = new DriveInfo(root).AvailableFreeSpace; }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or DriveNotFoundException) { result[root] = null; }
		}
		return result;
	}

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
			if (!target.Snapshot.IsMeasured)
			{
				return Markup.Escape($"{target.Name.Get(Language)} ({(Language == AppLanguage.Chinese ? "目录候选，大小未统计" : "directory candidate, size not measured")})");
			}
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
				: Markup.Escape($"{FormatBytes(Candidate!.Bytes)} · {Candidate.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd} · {Candidate.Path}");
	}

	private sealed record InstallerSelection(InstallerCandidate? Candidate, AppLanguage Language, bool IsCancel = false)
	{
		public static InstallerSelection Cancel(AppLanguage language)
			=> new(null, language, IsCancel: true);

		public override string ToString()
			=> IsCancel
				? Markup.Escape(GetCancelLabel(Language))
				: Markup.Escape($"{FormatBytes(Candidate!.Bytes)} · {Candidate.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd} · {Candidate.Source.Get(Language)} · {Candidate.Path}");
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
