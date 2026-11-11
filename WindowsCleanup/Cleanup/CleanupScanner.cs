namespace WindowsCleanup.Cleanup;

public static class CleanupScanner
{
	public static IReadOnlyList<CleanupTarget> ScanTargets(
		IEnumerable<CleanupTarget> targets,
		CancellationToken cancellationToken = default,
		Action<CleanupTarget>? onTargetScanned = null,
		bool directoryOnly = false)
	{
		var available = new List<CleanupTarget>();
		foreach (var target in targets)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (directoryOnly && target.Locations.All(location => location.Kind != CleanupLocationKind.RecycleBin))
			{
				var count = 0;
				foreach (var location in target.Locations)
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (SafeFileSystem.IsExistingSafeLocation(location.Path)) count++;
				}
				target.Snapshot = new CleanupSnapshot(0, 0, count, IsMeasured: false);
			}
			else ScanTarget(target, cancellationToken);
			if (target.Snapshot.FileCount > 0 || target.Snapshot.Bytes > 0 ||
				(!target.Snapshot.IsMeasured && target.Snapshot.LocationsWithFiles > 0))
			{
				available.Add(target);
			}

			onTargetScanned?.Invoke(target);
		}

		return available;
	}

	public static CleanupSnapshot ScanTarget(CleanupTarget target, CancellationToken cancellationToken = default)
	{
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var fileCount = 0;
		long bytes = 0;
		var locationsWithFiles = 0;
		var measuredPaths = new List<string>();

		foreach (var location in target.Locations)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var locationHasFiles = false;

			if (location.Kind == CleanupLocationKind.RecycleBin)
			{
				if (RecycleBinOperations.TryGetInfo(location.Path, out var recycleBinInfo))
				{
					fileCount += (int)Math.Min(recycleBinInfo.ItemCount, int.MaxValue);
					bytes += recycleBinInfo.Bytes;
					locationHasFiles = recycleBinInfo.ItemCount > 0 || recycleBinInfo.Bytes > 0;
				}
			}
			else
			{
				foreach (var entry in SafeFileSystem.EnumerateFiles(location, cancellationToken))
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (!seen.Add(entry.Path))
					{
						continue;
					}

					fileCount++;
					bytes += entry.Length;
					locationHasFiles = true;
				}
			}

			if (locationHasFiles)
			{
				locationsWithFiles++;
				measuredPaths.Add(location.Path);
			}
		}

		target.Snapshot = new CleanupSnapshot(fileCount, bytes, locationsWithFiles);
		target.MeasuredPaths = measuredPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		return target.Snapshot;
	}
}
