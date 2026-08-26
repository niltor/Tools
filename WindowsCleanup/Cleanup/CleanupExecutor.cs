namespace WindowsCleanup.Cleanup;

public static class CleanupExecutor
{
	public static CleanupExecutionResult Execute(CleanupTarget target, CancellationToken cancellationToken = default)
	{
		var deletedFiles = 0;
		long deletedBytes = 0;
		var failedFiles = 0;
		var errors = new List<string>();

		foreach (var location in target.Locations)
		{
			cancellationToken.ThrowIfCancellationRequested();
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
