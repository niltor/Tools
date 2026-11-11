namespace WindowsCleanup.Cleanup;

public static class CleanupExecutor
{
	public static CleanupExecutionResult Execute(CleanupTarget target, CancellationToken cancellationToken = default)
	{
		var deletedFiles = 0;
		long deletedBytes = 0;
		var failedFiles = 0;
		var errors = new List<string>();
		var trackedByRepository = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
		var ignoredByRepository = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

		foreach (var location in target.Locations)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (location.GitRepository is { } repository)
			{
				if (!trackedByRepository.TryGetValue(repository, out var tracked))
					trackedByRepository[repository] = tracked = GitBuildOutputs.TrackedPaths(repository, cancellationToken);
				var relative = GitBuildOutputs.Relative(repository, location.Path);
				if (location.RequireGitIgnore && !ignoredByRepository.ContainsKey(repository))
					ignoredByRepository[repository] = GitBuildOutputs.IgnoredDirectories(repository, target.Locations
						.Where(item => item.GitRepository == repository && item.RequireGitIgnore).Select(item => item.Path), cancellationToken);
				if (tracked is null || tracked.Contains(relative) || (location.RequireGitIgnore && !ignoredByRepository[repository].Contains(relative)))
				{
					failedFiles++;
					if (errors.Count < 5) errors.Add(location.Path + ": Git protection / Git 规则校验未通过");
					continue;
				}
			}
			CleanupExecutionResult result;
			if (location.Kind == CleanupLocationKind.RecycleBin)
			{
				result = RecycleBinOperations.Empty(location.Path, target.Snapshot);
			}
			else
			{
				result = SafeFileSystem.DeleteLocation(location);
			}

			deletedFiles += result.DeletedFiles;
			deletedBytes += result.DeletedBytes;
			failedFiles += result.FailedFiles;
			foreach (var error in result.Errors)
			{
				if (errors.Count < 5)
				{
					errors.Add(error);
				}
			}
		}

		return new CleanupExecutionResult(deletedFiles, deletedBytes, failedFiles, errors);
	}
}
