using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace WindowsManager.Cleanup;

internal static class GitBuildOutputs
{
	public static string? FindRepository(string path)
	{
		for (var folder = path; folder is not null; folder = Path.GetDirectoryName(folder))
		{
			var marker = Path.Combine(folder, ".git");
			if (File.Exists(marker) || Directory.Exists(marker)) return folder;
		}
		return null;
	}

	public static HashSet<string>? TrackedPaths(string repository, CancellationToken token)
	{
		var paths = Run(repository, ["ls-files", "--cached", "-z"], null, token);
		if (paths is null) return null;
		var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var path in paths)
		{
			var current = path;
			while (result.Add(current))
			{
				var separator = current.LastIndexOf('/');
				if (separator < 0) break;
				current = current[..separator];
			}
		}
		return result;
	}

	public static HashSet<string> IgnoredDirectories(string repository, IEnumerable<string> paths, CancellationToken token)
	{
		var names = paths.Select(path => Relative(repository, path) + "/").ToArray();
		if (names.Length == 0) return new(StringComparer.OrdinalIgnoreCase);
		return (Run(repository, ["check-ignore", "-z", "--stdin"], string.Join('\0', names) + '\0', token, allowNoMatch: true) ?? [])
			.Select(name => name.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
	}

	public static string Relative(string repository, string path)
		=> Path.GetRelativePath(repository, path).Replace('\\', '/').TrimEnd('/');

	private static string[]? Run(string repository, string[] args, string? input, CancellationToken token, bool allowNoMatch = false)
	{
		var info = new ProcessStartInfo("git")
		{
			UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
			RedirectStandardError = true, RedirectStandardInput = input is not null,
			StandardOutputEncoding = Encoding.UTF8
		};
		foreach (var key in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE" })
			info.Environment.Remove(key);
		foreach (var arg in new[] { "--no-optional-locks", "-C", repository }.Concat(args)) info.ArgumentList.Add(arg);
		try
		{
			using var process = Process.Start(info);
			if (process is null) return null;
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
			timeout.CancelAfter(TimeSpan.FromSeconds(10));
			var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
			var errors = process.StandardError.ReadToEndAsync(timeout.Token);
			try
			{
				if (input is not null)
				{
					process.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(input), timeout.Token).AsTask().GetAwaiter().GetResult();
					process.StandardInput.Close();
				}
				process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
				var text = output.GetAwaiter().GetResult();
				_ = errors.GetAwaiter().GetResult();
				return process.ExitCode == 0 || (allowNoMatch && process.ExitCode == 1)
					? text.Split('\0', StringSplitOptions.RemoveEmptyEntries) : null;
			}
			catch (OperationCanceledException)
			{
				try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
				token.ThrowIfCancellationRequested();
				return null;
			}
		}
		catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { return null; }
	}
}
