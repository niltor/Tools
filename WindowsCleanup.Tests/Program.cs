using System.ComponentModel;
using System.Diagnostics;
using WindowsCleanup;
using WindowsCleanup.Cleanup;
using WindowsCleanup.Localization;

var tests = new (string Name, Action Body)[]
{
	("project markers select known outputs", TestProjectMarkers),
	("project outputs include deployable build directories", TestProjectOutputs),
	("project outputs aggregate by project and honor git metadata", TestGitProjectRules),
	("solution projects aggregate by .sln and .slnx", TestSolutionAggregation),
	("workspace manifests aggregate Node and Cargo projects", TestWorkspaceAggregation),
	("unknown directories and pruned trees are ignored", TestPruning),
	("overlapping roots are deduplicated", TestOverlappingRoots),
	("cancellation stops discovery", TestCancellation),
	("reparse-point directories are not followed", TestReparsePoint),
	("developer CLI options and invalid combinations", TestCli),
	("executor deletes only the selected candidate", TestExecutor),
	("NuGet package markers protect incomplete packages", TestNuGetPackageCache),
	("cache paths below links are never followed", TestLinkedCacheAncestor),
	("developer catalog keeps review-only cache boundaries", TestDeveloperCatalog),
	("developer cache kinds separate build and package cleanup", TestDeveloperCacheKinds),
	("directory-only scan detects roots without reading files", TestDirectoryOnlyScan),
	("exact scan still measures files and bytes", TestExactScan),
	("directory-only scan honors cancellation and safety", TestDirectoryOnlySafety),
};

var failures = new List<string>();
foreach (var test in tests)
{
	try
	{
		test.Body();
		Console.WriteLine($"PASS {test.Name}");
	}
	catch (Exception exception)
	{
		failures.Add($"{test.Name}: {exception.Message}");
		Console.WriteLine($"FAIL {test.Name}: {exception.Message}");
	}
}

if (failures.Count > 0)
{
	Console.Error.WriteLine($"{failures.Count} test(s) failed.");
	return 1;
}

Console.WriteLine($"{tests.Length} tests passed.");
return 0;

static void TestProjectMarkers()
{
	WithTempDirectory(root =>
	{
		var dotnet = Directory.CreateDirectory(Path.Combine(root, "dotnet")).FullName;
		File.WriteAllText(Path.Combine(dotnet, "app.csproj"), "<Project />");
		Directory.CreateDirectory(Path.Combine(dotnet, "bin"));
		Directory.CreateDirectory(Path.Combine(dotnet, "obj"));
		var rust = Directory.CreateDirectory(Path.Combine(root, "rust")).FullName;
		File.WriteAllText(Path.Combine(rust, "Cargo.toml"), "[package]");
		Directory.CreateDirectory(Path.Combine(rust, "target"));
		File.WriteAllText(Path.Combine(rust, "target", "CACHEDIR.TAG"), "Signature: 8a477f597d28d172");
		var node = Directory.CreateDirectory(Path.Combine(root, "node")).FullName;
		File.WriteAllText(Path.Combine(node, "package.json"), "{}");
		Directory.CreateDirectory(Path.Combine(node, ".next"));

		var targets = ProjectCacheDiscovery.Discover(new[] { root });
		var dotnetTargets = targets.Where(target => target.Name.Get(AppLanguage.English).StartsWith(".NET:", StringComparison.Ordinal)).ToArray();
		Require(dotnetTargets.Length == 1, "a project should produce one .NET target");
		Require(dotnetTargets[0].Locations.Count == 2 && dotnetTargets[0].Locations.Any(location => location.Path.EndsWith("bin", StringComparison.OrdinalIgnoreCase)) && dotnetTargets[0].Locations.Any(location => location.Path.EndsWith("obj", StringComparison.OrdinalIgnoreCase)), "missing aggregated .NET locations");
		Require(targets.Any(target => target.Name.Get(AppLanguage.English).StartsWith("Rust:") && target.Locations.Any(location => location.Path.EndsWith("target", StringComparison.OrdinalIgnoreCase))), "missing Rust target");
		Require(targets.Any(target => target.Name.Get(AppLanguage.English).StartsWith("Node.js:") && target.Locations.Any(location => location.Path.EndsWith(".next", StringComparison.OrdinalIgnoreCase))), "missing Node output target");
	});
}

static void TestGitProjectRules()
{
	WithTempDirectory(root =>
	{
		if (!CanRunGit())
		{
			Console.WriteLine("  SKIP git is unavailable");
			return;
		}
		var project = Directory.CreateDirectory(Path.Combine(root, "git-project")).FullName;
		File.WriteAllText(Path.Combine(project, "app.csproj"), "<Project />");
		File.WriteAllText(Path.Combine(project, ".gitignore"), "artifacts/\n.env\nlocal/\n");
		Directory.CreateDirectory(Path.Combine(project, "artifacts"));
		File.WriteAllText(Path.Combine(project, "artifacts", "app.dll"), "generated");
		File.WriteAllText(Path.Combine(project, ".env"), "SECRET=do-not-delete");
		Directory.CreateDirectory(Path.Combine(project, "local"));
		File.WriteAllText(Path.Combine(project, "local", "settings.json"), "keep");
		Directory.CreateDirectory(Path.Combine(project, "bin"));
		File.WriteAllText(Path.Combine(project, "bin", "tracked.dll"), "tracked");
		RunGit(project, "init", "-q");
		RunGit(project, "add", ".gitignore");
		RunGit(project, "add", "-f", "bin/tracked.dll");

		var targets = ProjectCacheDiscovery.Discover(new[] { root });
		var projectTargets = targets.Where(target => target.Name.Get(AppLanguage.English).EndsWith(project, StringComparison.OrdinalIgnoreCase)).ToArray();
		Require(projectTargets.Length == 1, "git project outputs were not aggregated");
		var locations = projectTargets[0].Locations.Select(location => Path.GetFileName(location.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		Require(locations.Contains("artifacts"), "gitignored artifacts directory was not detected");
		Require(!locations.Contains(".env") && !locations.Contains("local"), "ignored personal data was selected");
		Require(!locations.Contains("bin"), "tracked build output was selected");
		RunGit(project, "add", "-f", "artifacts/app.dll");
		Require(CleanupExecutor.Execute(projectTargets[0]).DeletedFiles == 0, "newly tracked artifact was deleted");
		Require(File.Exists(Path.Combine(project, "artifacts", "app.dll")), "Git protection did not preserve the artifact");
		RunGit(project, "rm", "--cached", "-f", "artifacts/app.dll");
		Require(CleanupExecutor.Execute(projectTargets[0]).DeletedFiles == 1, "ignored build artifact was not deleted");
		Require(File.Exists(Path.Combine(project, ".env")) && File.Exists(Path.Combine(project, "local", "settings.json")),
			"project cleanup deleted ignored personal data");
	});
}

static void TestProjectOutputs()
{
	WithTempDirectory(root =>
	{
		var node = Directory.CreateDirectory(Path.Combine(root, "node")).FullName;
		File.WriteAllText(Path.Combine(node, "package.json"), "{}");
		foreach (var name in new[] { "dist", "build", ".next", ".nuxt", ".output", ".svelte-kit" })
			Directory.CreateDirectory(Path.Combine(node, name));
		Directory.CreateDirectory(Path.Combine(node, "node_modules", "lib"));
		File.WriteAllText(Path.Combine(node, "node_modules", "package-lock.json"), "keep");
		var python = Directory.CreateDirectory(Path.Combine(root, "python")).FullName;
		File.WriteAllText(Path.Combine(python, "pyproject.toml"), "[build-system]");
		Directory.CreateDirectory(Path.Combine(python, "build"));
		Directory.CreateDirectory(Path.Combine(python, "dist"));
		var targets = ProjectCacheDiscovery.Discover(new[] { root });
		var nodePaths = targets.Where(target => target.Name.Get(AppLanguage.English).StartsWith("Node.js:", StringComparison.Ordinal))
			.SelectMany(target => target.Locations).Select(location => Path.GetFileName(location.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		Require(new[] { "dist", "build", ".next", ".nuxt", ".output", ".svelte-kit" }.All(nodePaths.Contains), "Node build output was not selected");
		Require(!targets.SelectMany(target => target.Locations).Any(location => location.Path.Contains("node_modules", StringComparison.OrdinalIgnoreCase)), "node_modules dependency tree was selected");
		Require(targets.Any(target => target.Name.Get(AppLanguage.English).StartsWith("Python:", StringComparison.Ordinal) &&
			target.Locations.Any(location => location.Path.EndsWith("build", StringComparison.OrdinalIgnoreCase))), "Python build output was not selected");
	});
}

static void TestSolutionAggregation()
{
	WithTempDirectory(root =>
	{
		var app = Directory.CreateDirectory(Path.Combine(root, "src", "App")).FullName;
		File.WriteAllText(Path.Combine(app, "App.csproj"), "<Project />");
		Directory.CreateDirectory(Path.Combine(app, "bin"));
		Directory.CreateDirectory(Path.Combine(app, "obj"));
		var library = Directory.CreateDirectory(Path.Combine(root, "src", "Library")).FullName;
		File.WriteAllText(Path.Combine(library, "Library.csproj"), "<Project />");
		Directory.CreateDirectory(Path.Combine(library, "bin"));
		Directory.CreateDirectory(Path.Combine(library, "obj"));
		var modern = Directory.CreateDirectory(Path.Combine(root, "src", "Modern")).FullName;
		File.WriteAllText(Path.Combine(modern, "Modern.csproj"), "<Project />");
		Directory.CreateDirectory(Path.Combine(modern, "bin"));
		Directory.CreateDirectory(Path.Combine(modern, "obj"));
		var standalone = Directory.CreateDirectory(Path.Combine(root, "src", "Standalone")).FullName;
		File.WriteAllText(Path.Combine(standalone, "Standalone.csproj"), "<Project />");
		Directory.CreateDirectory(Path.Combine(standalone, "bin"));
		Directory.CreateDirectory(Path.Combine(standalone, "obj"));

		var slnPath = Path.Combine(root, "Suite.sln");
		File.WriteAllText(slnPath, string.Join(Environment.NewLine, new[]
		{
			"Microsoft Visual Studio Solution File, Format Version 12.00",
			"# Visual Studio Version 17",
			"Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"App\", \"src\\\\App\\\\App.csproj\", \"{11111111-1111-1111-1111-111111111111}\"",
			"EndProject",
			"Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Library\", \"src\\\\Library\\\\Library.csproj\", \"{22222222-2222-2222-2222-222222222222}\"",
			"EndProject",
			"Global",
			"EndGlobal"
		}));
		var slnxPath = Path.Combine(root, "Modern.slnx");
		File.WriteAllText(slnxPath, string.Join(Environment.NewLine, new[]
		{
			"<Solution>",
			"  <Project Path=\"src/Modern/Modern.csproj\" />",
			"</Solution>"
		}));

		var targets = ProjectCacheDiscovery.Discover(new[] { root });
		var english = targets.Select(target => (Target: target, Name: target.Name.Get(AppLanguage.English))).ToArray();
		var solutionTargets = english.Where(item => item.Name.Contains(slnPath, StringComparison.OrdinalIgnoreCase) ||
			item.Name.Contains(slnxPath, StringComparison.OrdinalIgnoreCase)).ToArray();
		Require(solutionTargets.Length == 2, "each solution should produce one aggregate target");
		var slnTarget = solutionTargets.Single(item => item.Name.Contains(slnPath, StringComparison.OrdinalIgnoreCase)).Target;
		Require(slnTarget.Locations.Count == 4 &&
			slnTarget.Locations.Any(location => location.Path.StartsWith(app, StringComparison.OrdinalIgnoreCase)) &&
			slnTarget.Locations.Any(location => location.Path.StartsWith(library, StringComparison.OrdinalIgnoreCase)),
			".sln projects were not aggregated into one solution target");
		var slnxTarget = solutionTargets.Single(item => item.Name.Contains(slnxPath, StringComparison.OrdinalIgnoreCase)).Target;
		Require(slnxTarget.Locations.Count == 2 && slnxTarget.Locations.All(location => location.Path.StartsWith(modern, StringComparison.OrdinalIgnoreCase)),
			".slnx project was not aggregated into one solution target");

		var standaloneTargets = english.Where(item => item.Target.Locations.Any(location => location.Path.StartsWith(standalone, StringComparison.OrdinalIgnoreCase))).ToArray();
		Require(standaloneTargets.Length == 1 && !standaloneTargets[0].Name.Contains(".sln", StringComparison.OrdinalIgnoreCase) &&
			!standaloneTargets[0].Name.Contains(".slnx", StringComparison.OrdinalIgnoreCase),
			"project not referenced by a solution should remain an individual target");
	});
}

static void TestWorkspaceAggregation()
{
	WithTempDirectory(root =>
	{
		var web = Directory.CreateDirectory(Path.Combine(root, "web")).FullName;
		File.WriteAllText(Path.Combine(web, "package.json"), "{\"workspaces\": [\"packages/*\", \"!packages/ignored\"]}");
		var webApp = CreateNodeWorkspaceProject(web, "app");
		var webShared = CreateNodeWorkspaceProject(web, "shared");
		var webIgnored = CreateNodeWorkspaceProject(web, "ignored");
		var cargo = Directory.CreateDirectory(Path.Combine(root, "native")).FullName;
		File.WriteAllText(Path.Combine(cargo, "Cargo.toml"), string.Join(Environment.NewLine, new[]
		{
			"[workspace]",
			"members = [",
			"  \"crates/core\",",
			"  \"crates/cli\",",
			"]"
		}));
		var cargoCore = CreateCargoWorkspaceProject(cargo, "core");
		var cargoCli = CreateCargoWorkspaceProject(cargo, "cli");

		var targets = ProjectCacheDiscovery.Discover(new[] { root });
		var english = targets.Select(target => (Target: target, Name: target.Name.Get(AppLanguage.English))).ToArray();
		var nodeWorkspace = english.Single(item => item.Name.Contains(Path.Combine(web, "package.json"), StringComparison.OrdinalIgnoreCase));
		Require(nodeWorkspace.Target.Locations.Count == 4 &&
			nodeWorkspace.Target.Locations.Any(location => location.Path.StartsWith(webApp, StringComparison.OrdinalIgnoreCase)) &&
			nodeWorkspace.Target.Locations.Any(location => location.Path.StartsWith(webShared, StringComparison.OrdinalIgnoreCase)),
			"Node workspace members were not aggregated");
		Require(!nodeWorkspace.Target.Locations.Any(location => location.Path.StartsWith(webIgnored, StringComparison.OrdinalIgnoreCase)),
			"Node workspace exclusion was ignored");
		Require(english.Count(item => item.Target.Locations.Any(location => location.Path.StartsWith(webIgnored, StringComparison.OrdinalIgnoreCase))) == 1,
			"excluded Node workspace member did not remain an individual target");

		var cargoWorkspace = english.Single(item => item.Name.Contains(Path.Combine(cargo, "Cargo.toml"), StringComparison.OrdinalIgnoreCase));
		Require(cargoWorkspace.Target.Locations.Count == 2 &&
			cargoWorkspace.Target.Locations.Any(location => location.Path.StartsWith(cargoCore, StringComparison.OrdinalIgnoreCase)) &&
			cargoWorkspace.Target.Locations.Any(location => location.Path.StartsWith(cargoCli, StringComparison.OrdinalIgnoreCase)),
			"Cargo workspace members were not aggregated from the multi-line manifest");
	});
}

static string CreateNodeWorkspaceProject(string workspace, string name)
{
	var project = Directory.CreateDirectory(Path.Combine(workspace, "packages", name)).FullName;
	File.WriteAllText(Path.Combine(project, "package.json"), "{}");
	Directory.CreateDirectory(Path.Combine(project, "dist"));
	Directory.CreateDirectory(Path.Combine(project, "build"));
	return project;
}

static string CreateCargoWorkspaceProject(string workspace, string name)
{
	var project = Directory.CreateDirectory(Path.Combine(workspace, "crates", name)).FullName;
	File.WriteAllText(Path.Combine(project, "Cargo.toml"), "[package]");
	var target = Directory.CreateDirectory(Path.Combine(project, "target")).FullName;
	File.WriteAllText(Path.Combine(target, "CACHEDIR.TAG"), "Signature: 8a477f597d28d172");
	return project;
}

static void TestPruning()
{
	WithTempDirectory(root =>
	{
		var unknown = Directory.CreateDirectory(Path.Combine(root, "unknown")).FullName;
		Directory.CreateDirectory(Path.Combine(unknown, "bin"));
		var modules = Directory.CreateDirectory(Path.Combine(root, "node_modules", "package")).FullName;
		File.WriteAllText(Path.Combine(modules, "app.csproj"), "<Project />");
		Directory.CreateDirectory(Path.Combine(modules, "bin"));
		var git = Directory.CreateDirectory(Path.Combine(root, ".git", "nested")).FullName;
		File.WriteAllText(Path.Combine(git, "app.csproj"), "<Project />");
		Directory.CreateDirectory(Path.Combine(git, "obj"));

		var targets = ProjectCacheDiscovery.Discover(new[] { root });
		Require(!targets.Any(target => target.Locations.Any(location => location.Path.Contains("unknown", StringComparison.OrdinalIgnoreCase))), "unknown bin was selected");
		Require(!targets.Any(target => target.Locations.Any(location => location.Path.Contains("node_modules", StringComparison.OrdinalIgnoreCase))), "node_modules was traversed");
		Require(!targets.Any(target => target.Locations.Any(location => location.Path.Contains(".git", StringComparison.OrdinalIgnoreCase))), ".git was traversed");
	});
}

static void TestOverlappingRoots()
{
	WithTempDirectory(root =>
	{
		var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
		File.WriteAllText(Path.Combine(project, "app.csproj"), "<Project />");
		Directory.CreateDirectory(Path.Combine(project, "bin"));
		var targets = ProjectCacheDiscovery.Discover(new[] { root, project, root });
		var locations = targets.SelectMany(target => target.Locations).Select(location => location.Path).ToArray();
		Require(locations.Length == locations.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "duplicate output location returned");
		Require(locations.Count(path => path.EndsWith("bin", StringComparison.OrdinalIgnoreCase)) == 1, "overlapping roots duplicated bin");
	});
}

static void TestCancellation()
{
	WithTempDirectory(root =>
	{
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		var stopped = false;
		try
		{
			ProjectCacheDiscovery.Discover(new[] { root }, cancellation.Token);
		}
		catch (OperationCanceledException)
		{
			stopped = true;
		}
		Require(stopped, "cancelled discovery did not throw");
	});
}

static void TestReparsePoint()
{
	WithTempDirectory(root =>
	{
		var external = Directory.CreateDirectory(Path.Combine(root, "external")).FullName;
		File.WriteAllText(Path.Combine(external, "app.csproj"), "<Project />");
		Directory.CreateDirectory(Path.Combine(external, "bin"));
		var link = Path.Combine(root, "linked-project");
		try
		{
			Directory.CreateSymbolicLink(link, external);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			Console.WriteLine("  SKIP reparse-point creation is unavailable");
			return;
		}
		var targets = ProjectCacheDiscovery.Discover(new[] { root });
		Require(!targets.Any(target => target.Locations.Any(location => location.Path.StartsWith(link, StringComparison.OrdinalIgnoreCase))), "symbolic link was followed");
	});
}

static void TestCli()
{
	WithTempDirectory(root =>
	{
		Require(CliOptionsParser.TryParse(new[] { "--developer", "--project-root", root, "--dry-run" }, out var options, out _), "valid developer CLI was rejected");
		Require(options.Command == CliCommand.Developer && options.DryRun && options.ProjectRoots?.Count == 1, "developer CLI options were not retained");
		Require(!CliOptionsParser.TryParse(new[] { "--clean", "--project-root", root }, out _, out _), "project root accepted with --clean");
		Require(!CliOptionsParser.TryParse(new[] { "--developer", "--all" }, out _, out _), "--all accepted with --developer");
		Require(!CliOptionsParser.TryParse(new[] { "--project-root", root }, out _, out _), "project root accepted without --developer");
		Require(CliOptionsParser.TryParse(new[] { "--developer-packages", "--dry-run" }, out var packages, out _), "developer package CLI was rejected");
		Require(packages.Command == CliCommand.DeveloperPackages && packages.DryRun, "developer package CLI options were not retained");
		Require(!CliOptionsParser.TryParse(new[] { "--developer-packages", "--project-root", root }, out _, out _), "project root accepted with developer packages");
		Require(!CliOptionsParser.TryParse(new[] { "--developer-packages", "--all" }, out _, out _), "--all accepted with developer packages");
	});
}

static void TestExecutor()
{
	WithTempDirectory(root =>
	{
		var candidate = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
		var source = Path.Combine(root, "source.cs");
		var generated = Path.Combine(candidate, "app.dll");
		File.WriteAllText(source, "keep");
		File.WriteAllText(generated, "remove");
		var target = new CleanupTarget("test", new LocalizedText("test", "test"),
			new LocalizedText("candidate", "candidate"), new LocalizedText("test", "test"),
			new[] { new CleanupLocation(candidate) });
		var result = CleanupExecutor.Execute(target);
		Require(result.DeletedFiles == 1 && !File.Exists(generated), "candidate file was not removed");
		Require(File.Exists(source), "source file was removed");
	});
}

static void TestNuGetPackageCache()
{
	WithTempDirectory(root =>
	{
		var package = Directory.CreateDirectory(Path.Combine(root, "package-locked")).FullName;
		var metadata = Path.Combine(package, ".nupkg.metadata");
		var payload = Path.Combine(package, "lib.dll");
		File.WriteAllText(metadata, "complete");
		File.WriteAllText(payload, "payload");
		var target = CreateTarget("nuget-locked", new CleanupLocation(package, NuGetPackageCache: true));
		using (var lockHandle = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			var result = CleanupExecutor.Execute(target);
			if (OperatingSystem.IsWindows())
			{
				Require(result.FailedFiles > 0 && File.Exists(metadata) && File.Exists(payload), "locked NuGet marker did not preserve package");
			}
		}

		var completePackage = Directory.CreateDirectory(Path.Combine(root, "package-complete")).FullName;
		File.WriteAllText(Path.Combine(completePackage, ".nupkg.metadata"), "complete");
		File.WriteAllText(Path.Combine(completePackage, "lib.dll"), "payload");
		Directory.CreateDirectory(Path.Combine(completePackage, "nested"));
		File.WriteAllText(Path.Combine(completePackage, "nested", "readme.txt"), "payload");
		var completeResult = CleanupExecutor.Execute(CreateTarget("nuget-complete", new CleanupLocation(completePackage, NuGetPackageCache: true)));
		Require(completeResult.DeletedFiles == 3 && !File.Exists(Path.Combine(completePackage, ".nupkg.metadata")) &&
			!File.Exists(Path.Combine(completePackage, "lib.dll")) && !Directory.EnumerateFileSystemEntries(completePackage).Any(),
			"complete NuGet package was not fully removed after marker");
	});
}

static void TestLinkedCacheAncestor()
{
	WithTempDirectory(root =>
	{
		var external = Directory.CreateDirectory(Path.Combine(root, "external")).FullName;
		var cache = Directory.CreateDirectory(Path.Combine(external, "cache")).FullName;
		var payload = Path.Combine(cache, "payload.bin");
		File.WriteAllText(payload, "keep");
		var link = Path.Combine(root, "linked-root");
		try
		{
			Directory.CreateSymbolicLink(link, external);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			Console.WriteLine("  SKIP link creation is unavailable");
			return;
		}
		var result = CleanupExecutor.Execute(CreateTarget("linked-cache", new CleanupLocation(Path.Combine(link, "cache"))));
		Require(result.DeletedFiles == 0 && File.Exists(payload), "cache below a link ancestor was followed");
	});
}

static void TestDeveloperCatalog()
{
	var targets = DeveloperCacheCatalog.Build();
	Require(targets.Count > 0, "developer catalog is empty");
	Require(targets.All(target => target.OptIn && target.Risk == CleanupRisk.Moderate), "developer target is included as default-safe");
	var paths = targets.SelectMany(target => target.Locations).Select(location => location.Path).ToArray();
	Require(!paths.Any(path => path.Contains(Path.Combine(".m2", "repository"), StringComparison.OrdinalIgnoreCase)), "Maven repository was included");
	Require(!paths.Any(path => path.Contains(Path.Combine(".cargo", "registry", "src"), StringComparison.OrdinalIgnoreCase)), "Cargo source was included");
	Require(!paths.Any(path => path.Contains(Path.Combine(".cargo", "git"), StringComparison.OrdinalIgnoreCase)), "Cargo git cache was included");
	Require(!paths.Any(path => path.Contains(".rustup", StringComparison.OrdinalIgnoreCase)), "rustup was included");
}

static void TestDeveloperCacheKinds()
{
	var all = DeveloperCacheCatalog.Build();
	var builds = DeveloperCacheCatalog.Build(DeveloperCacheKind.ProjectBuild);
	var packages = DeveloperCacheCatalog.Build(DeveloperCacheKind.Packages);
	var tools = DeveloperCacheCatalog.Build(DeveloperCacheKind.Tools);
	Require(all.Count == builds.Count + packages.Count + tools.Count, "developer cache kinds overlap or omit targets");
	Require(builds.Count > 0 && packages.Count > 0 && tools.Count > 0, "developer cache kind is empty");
	Require(builds.All(target => target.DeveloperKind == DeveloperCacheKind.ProjectBuild), "build selection contains non-build cache");
	Require(packages.All(target => target.DeveloperKind == DeveloperCacheKind.Packages), "package selection contains non-package cache");
	Require(tools.All(target => target.DeveloperKind == DeveloperCacheKind.Tools), "tool selection contains non-tool cache");
	Require(!builds.Any(target => target.Name.Get(AppLanguage.English).Contains("npm", StringComparison.OrdinalIgnoreCase) ||
		target.Name.Get(AppLanguage.English).Contains("NuGet", StringComparison.OrdinalIgnoreCase) ||
		target.Name.Get(AppLanguage.English).Contains("Cargo", StringComparison.OrdinalIgnoreCase) ||
		target.Name.Get(AppLanguage.English).Contains("download", StringComparison.OrdinalIgnoreCase)),
		"routine project-build selection includes package downloads");
}

static void TestDirectoryOnlyScan()
{
	WithTempDirectory(root =>
	{
		var empty = Directory.CreateDirectory(Path.Combine(root, "empty-cache")).FullName;
		var populated = Directory.CreateDirectory(Path.Combine(root, "populated-cache")).FullName;
		File.WriteAllText(Path.Combine(populated, "large.bin"), new string('x', 4096));
		var missing = Path.Combine(root, "missing-cache");
		var targets = new[]
		{
			CreateTarget("empty", new CleanupLocation(empty)),
			CreateTarget("populated", new CleanupLocation(populated)),
			CreateTarget("missing", new CleanupLocation(missing))
		};
		var available = CleanupScanner.ScanTargets(targets, directoryOnly: true);
		Require(available.Count == 2, "directory-only scan did not distinguish existing roots");
		Require(available.All(target => !target.Snapshot.IsMeasured), "directory-only snapshot was marked measured");
		Require(available.All(target => target.Snapshot.FileCount == 0 && target.Snapshot.Bytes == 0 && target.Snapshot.LocationsWithFiles == 1), "directory-only snapshot unexpectedly enumerated files");
	});
}

static void TestExactScan()
{
	WithTempDirectory(root =>
	{
		var cache = Directory.CreateDirectory(Path.Combine(root, "cache")).FullName;
		File.WriteAllBytes(Path.Combine(cache, "a.bin"), new byte[7]);
		File.WriteAllBytes(Path.Combine(cache, "b.bin"), new byte[11]);
		var target = CreateTarget("exact", new CleanupLocation(cache));
		var available = CleanupScanner.ScanTargets(new[] { target });
		Require(available.Count == 1 && target.Snapshot.IsMeasured, "exact scan was not measured");
		Require(target.Snapshot.FileCount == 2 && target.Snapshot.Bytes == 18 && target.Snapshot.LocationsWithFiles == 1, "exact scan produced incorrect count or size");
	});
}

static void TestDirectoryOnlySafety()
{
	WithTempDirectory(root =>
	{
		var protectedLike = Path.Combine(root, "protected-like");
		Directory.CreateDirectory(protectedLike);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		var cancelled = false;
		try
		{
			CleanupScanner.ScanTargets(new[] { CreateTarget("cancelled", new CleanupLocation(protectedLike)) }, cancellation.Token, directoryOnly: true);
		}
		catch (OperationCanceledException) { cancelled = true; }
		Require(cancelled, "directory-only scan ignored cancellation");

		var external = Directory.CreateDirectory(Path.Combine(root, "external")).FullName;
		var link = Path.Combine(root, "linked");
		try { Directory.CreateSymbolicLink(link, external); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			Console.WriteLine("  SKIP link creation is unavailable");
			return;
		}
		var available = CleanupScanner.ScanTargets(new[] { CreateTarget("linked", new CleanupLocation(Path.Combine(link, "cache"))) }, directoryOnly: true);
		Require(available.Count == 0, "directory-only scan followed a link ancestor");
	});
}

static CleanupTarget CreateTarget(string id, CleanupLocation location)
{
	return new CleanupTarget(id, new LocalizedText("test", "test"),
		new LocalizedText(id, id), new LocalizedText("test", "test"), new[] { location },
		risk: CleanupRisk.Moderate, optIn: true);
}

static void WithTempDirectory(Action<string> action)
{
	var path = Path.Combine(Path.GetTempPath(), "winclean-tests-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(path);
	try
	{
		action(path);
	}
	finally
	{
		if (Directory.Exists(path))
		{
			foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
			{
				try { File.SetAttributes(file, FileAttributes.Normal); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
			Directory.Delete(path, recursive: true);
		}
	}
}

static bool CanRunGit()
{
	try
	{
		using var process = Process.Start(new ProcessStartInfo("git", "--version")
		{
			UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
		});
		if (process is null) return false;
		process.WaitForExit(5000);
		return process.ExitCode == 0;
	}
	catch (Win32Exception) { return false; }
}

static void RunGit(string workingDirectory, params string[] arguments)
{
	var startInfo = new ProcessStartInfo("git")
	{
		WorkingDirectory = workingDirectory,
		UseShellExecute = false,
		RedirectStandardOutput = true,
		RedirectStandardError = true,
		CreateNoWindow = true
	};
	foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
	using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git did not start");
	var error = process.StandardError.ReadToEnd();
	process.WaitForExit(5000);
	if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
}

static void Require(bool condition, string message)
{
	if (!condition)
	{
		throw new InvalidOperationException(message);
	}
}
