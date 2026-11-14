namespace WindowsManager.Cleanup;

/// <summary>Discover known output directories without enumerating their contents.</summary>
public static class ProjectCacheDiscovery
{
	private static readonly HashSet<string> ExtraOutputs = new(StringComparer.OrdinalIgnoreCase)
		{ "artifacts", ".artifacts", "TestResults", "coverage", "out" };
	private static readonly HashSet<string> Pruned = new(StringComparer.OrdinalIgnoreCase)
	{
		".git", ".hg", ".svn", "node_modules", "vendor", ".venv", "venv", "AppData",
		"bin", "obj", "target", "build", "dist", ".next", ".nuxt", ".gradle",
		".cache", "__pycache__", ".terraform", ".cargo", ".rustup", ".nuget",
		".parcel-cache", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".angular"
	};

	public static IReadOnlyList<string> DefaultRoots()
	{
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
		return new[] { "source/repos", "repos", "projects", "go/src" }
			.Select(relative => Path.Combine(home, relative))
			.Concat(string.IsNullOrEmpty(systemDrive) ? [] : new[] { Path.Combine(systemDrive, "codes") })
			.Append(Environment.CurrentDirectory)
			.Where(path => Directory.Exists(path) && DeveloperCacheCatalog.IsCachePath(path))
			.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
	}

	public static IReadOnlyList<CleanupTarget> Discover(IEnumerable<string> roots, CancellationToken cancellationToken = default)
	{
		var candidates = new List<(string Project, string Language, string Path, bool RequireIgnore)>();
		var manifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var pending = new Stack<(string Path, int Depth)>();
		foreach (var root in roots)
		{
			if (DeveloperCacheCatalog.IsCachePath(root) && Directory.Exists(root)) pending.Push((Path.GetFullPath(root), 0));
		}
		while (pending.TryPop(out var item))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!visited.Add(item.Path) || SafeFileSystem.HasReparsePointAncestor(item.Path)) continue;
			manifests.UnionWith(WorkspaceGroups.FindManifests(item.Path));
			var children = Directories(item.Path);
			var childNames = children.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
			void Add(string language, params string[] names)
			{
				foreach (var name in names)
				{
					var path = Path.Combine(item.Path, name);
					if (!Directory.Exists(path) || !DeveloperCacheCatalog.IsCachePath(path) || !outputs.Add(path)) continue;
					candidates.Add((item.Path, language, path, false));
				}
			}
			bool Has(string name) => File.Exists(Path.Combine(item.Path, name));
			if ((childNames.Contains("bin") || childNames.Contains("obj")) && HasProjectFile(item.Path)) Add(".NET", "bin", "obj");
			if (Has("Cargo.toml") && File.Exists(Path.Combine(item.Path, "target", "CACHEDIR.TAG"))) Add("Rust", "target");
			if (Has("package.json")) Add("Node.js", "dist", "build", ".next", ".nuxt", ".output", ".svelte-kit", Path.Combine("node_modules", ".cache"), ".parcel-cache", ".angular/cache");
			if (Has("pom.xml")) Add("Maven", "target");
			if (Has("build.gradle") || Has("build.gradle.kts")) Add("Gradle", "build", ".gradle");
			if (childNames.Overlaps(new[] { "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache" }) &&
				(Has("pyproject.toml") || Has("setup.py") || HasFile(item.Path, "*.py")))
				Add("Python", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache");
			if (Has("pyproject.toml") || Has("setup.py")) Add("Python", "build", "dist");
			foreach (var child in children.Where(child => ExtraOutputs.Contains(Path.GetFileName(child))))
			{
				var repository = GitBuildOutputs.FindRepository(item.Path);
				if (repository is null || !DeveloperCacheCatalog.IsCachePath(child) || !outputs.Add(child)) continue;
				var project = FindProject(item.Path, repository);
				candidates.Add((project.Path, project.Language, child, true));
			}
			// A matched output is never traversed looking for more projects.
			if (item.Depth >= 8) continue;
			foreach (var child in children)
			{
				if (!Pruned.Contains(Path.GetFileName(child)) && !outputs.Contains(child) && DeveloperCacheCatalog.IsCachePath(child))
					pending.Push((child, item.Depth + 1));
			}
		}
		var accepted = new List<(string Project, string Language, CleanupLocation Location)>();
		foreach (var repoGroup in candidates.GroupBy(candidate => GitBuildOutputs.FindRepository(Path.GetDirectoryName(candidate.Path)!)))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var repo = repoGroup.Key;
			var tracked = repo is null ? null : GitBuildOutputs.TrackedPaths(repo, cancellationToken);
			if (repo is not null && tracked is null) continue;
			var ignored = repo is null ? null : GitBuildOutputs.IgnoredDirectories(repo,
				repoGroup.Where(candidate => candidate.RequireIgnore).Select(candidate => candidate.Path), cancellationToken);
			foreach (var candidate in repoGroup)
			{
				var relative = repo is null ? "" : GitBuildOutputs.Relative(repo, candidate.Path);
				if (tracked?.Contains(relative) == true || (candidate.RequireIgnore && ignored?.Contains(relative) != true)) continue;
				accepted.Add((candidate.Project, candidate.Language,
					new CleanupLocation(candidate.Path, GitRepository: repo, RequireGitIgnore: candidate.RequireIgnore)));
			}
		}

		var workspaces = WorkspaceGroups.Discover(manifests, accepted.Select(candidate => candidate.Project)
			.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken);
		var grouped = accepted.Select(candidate =>
		{
			var workspace = workspaces.FirstOrDefault(group => group.Members.Contains(candidate.Project) ||
				(candidate.Language == "Git" && string.Equals(Path.GetDirectoryName(group.Manifest), candidate.Project,
					StringComparison.OrdinalIgnoreCase)));
			return (Key: workspace?.Manifest ?? candidate.Project, Workspace: workspace, Candidate: candidate);
		});
		return grouped.GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).Select(project =>
		{
			var workspace = project.First().Workspace;
			var languages = workspace?.Label ?? string.Join(" / ", project.Select(entry => entry.Candidate.Language).Distinct().Order(StringComparer.Ordinal));
			var name = languages + ": " + project.Key;
			return new CleanupTarget("dev-project-" + project.Key, DeveloperCacheCatalog.BuildCategory, new(name, name),
				new("合并统计此项下的生成目录，清理后需重新构建。",
					"Combined build outputs for this entry; rebuild after cleanup."),
				project.Select(entry => entry.Candidate.Location), risk: CleanupRisk.Moderate, optIn: true, developerKind: DeveloperCacheKind.ProjectBuild);
		}).ToArray();
	}

	private static (string Path, string Language) FindProject(string path, string repository)
	{
		for (var folder = path; folder is not null; folder = Path.GetDirectoryName(folder))
		{
			if (HasProjectFile(folder)) return (folder, ".NET");
			if (File.Exists(Path.Combine(folder, "Cargo.toml"))) return (folder, "Rust");
			if (File.Exists(Path.Combine(folder, "package.json"))) return (folder, "Node.js");
			if (File.Exists(Path.Combine(folder, "pom.xml"))) return (folder, "Maven");
			if (File.Exists(Path.Combine(folder, "build.gradle")) || File.Exists(Path.Combine(folder, "build.gradle.kts"))) return (folder, "Gradle");
			if (File.Exists(Path.Combine(folder, "pyproject.toml")) || File.Exists(Path.Combine(folder, "setup.py"))) return (folder, "Python");
			if (File.Exists(Path.Combine(folder, "go.mod"))) return (folder, "Go");
			if (string.Equals(folder, repository, StringComparison.OrdinalIgnoreCase)) break;
		}
		return (repository, "Git");
	}

	internal static string[] Directories(string root)
	{
		try
		{
			if (!Directory.Exists(root) || SafeFileSystem.HasReparsePointAncestor(root)) return [];
			return Directory.GetDirectories(root).Where(path => !SafeFileSystem.IsReparsePoint(path)).ToArray();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
	}

	private static bool HasProjectFile(string root) => HasFile(root, "*.csproj") || HasFile(root, "*.fsproj") || HasFile(root, "*.vbproj");
	private static bool HasFile(string root, string pattern)
	{
		try { return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Any(); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
	}
}
