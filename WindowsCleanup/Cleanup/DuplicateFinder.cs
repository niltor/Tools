using System.Security.Cryptography;

namespace WindowsCleanup.Cleanup;

public sealed record DuplicateGroup(long Size, IReadOnlyList<string> Files);

public sealed record DuplicateDeleteResult(int DeletedFiles, long DeletedBytes, int FailedFiles, IReadOnlyList<string> Errors);

public sealed class DuplicateFinder
{
	private const long MinimumCandidateSize = 4 * 1024;
	private const int MaximumScannedFiles = 100_000;

	public static IReadOnlyList<string> GetDefaultRoots()
	{
		var roots = new[]
		{
			Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
			Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
			Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
			Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
		};

		return roots
			.Where(path => !string.IsNullOrWhiteSpace(path) &&
				!UserFolderDiscovery.IsSameOrDescendant(path, UserFolderDiscovery.GetDownloadsPath()) &&
				Directory.Exists(path) &&
				!SafeFileSystem.IsReparsePoint(path))
			.Select(path => Path.GetFullPath(path!))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	public async Task<IReadOnlyList<DuplicateGroup>> FindAsync(
		IEnumerable<string> roots,
		CancellationToken cancellationToken = default)
	{
		var byLength = new Dictionary<long, List<string>>();
		var scannedFiles = 0;

		foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			var location = new CleanupLocation(root);
			foreach (var entry in SafeFileSystem.EnumerateFiles(location))
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (entry.Length < MinimumCandidateSize)
				{
					continue;
				}

				scannedFiles++;
				if (scannedFiles > MaximumScannedFiles)
				{
					break;
				}

				if (!byLength.TryGetValue(entry.Length, out var paths))
				{
					paths = new List<string>();
					byLength.Add(entry.Length, paths);
				}

				paths.Add(entry.Path);
			}

			if (scannedFiles > MaximumScannedFiles)
			{
				break;
			}
		}

		var groups = new List<DuplicateGroup>();
		foreach (var lengthGroup in byLength.Where(pair => pair.Value.Count > 1))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var byHash = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
			foreach (var path in lengthGroup.Value)
			{
				var hash = await TryHashAsync(path, cancellationToken).ConfigureAwait(false);
				if (hash is null)
				{
					continue;
				}

				if (!byHash.TryGetValue(hash, out var paths))
				{
					paths = new List<string>();
					byHash.Add(hash, paths);
				}

				paths.Add(path);
			}

			foreach (var hashGroup in byHash.Values.Where(paths => paths.Count > 1))
			{
				groups.Add(new DuplicateGroup(lengthGroup.Key, hashGroup.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()));
			}
		}

		return groups
			.OrderByDescending(group => group.Size)
			.ThenByDescending(group => group.Files.Count)
			.ToArray();
	}

	public static DuplicateDeleteResult DeleteDuplicates(DuplicateGroup group)
	{
		var deletedFiles = 0;
		long deletedBytes = 0;
		var failedFiles = 0;
		var errors = new List<string>();

		foreach (var path in group.Files.Skip(1))
		{
			try
			{
				if (SafeFileSystem.IsReparsePoint(path) || !File.Exists(path))
				{
					continue;
				}

				var length = new FileInfo(path).Length;
				File.Delete(path);
				deletedFiles++;
				deletedBytes += length;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
			{
				failedFiles++;
				if (errors.Count < 5)
				{
					errors.Add($"{path}: {exception.Message}");
				}
			}
		}

		return new DuplicateDeleteResult(deletedFiles, deletedBytes, failedFiles, errors);
	}

	private static async Task<string?> TryHashAsync(string path, CancellationToken cancellationToken)
	{
		try
		{
			await using var stream = new FileStream(path, new FileStreamOptions
			{
				Mode = FileMode.Open,
				Access = FileAccess.Read,
				Share = FileShare.ReadWrite | FileShare.Delete,
				Options = FileOptions.SequentialScan
			});
			var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
			return Convert.ToHexString(hash);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return null;
		}
	}

	private static string? Combine(string? root, string part)
	{
		if (string.IsNullOrWhiteSpace(root))
		{
			return null;
		}

		try
		{
			return Path.Combine(root, part);
		}
		catch (ArgumentException)
		{
			return null;
		}
	}
}
