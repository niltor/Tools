using System.Runtime.InteropServices;

namespace WindowsCleanup.Cleanup;

internal readonly record struct RecycleBinInfo(long ItemCount, long Bytes);

internal static class RecycleBinOperations
{
	private const uint EmptyNoConfirmation = 0x00000001;
	private const uint EmptyNoProgressUi = 0x00000002;
	private const uint EmptyNoSound = 0x00000004;

	public static bool TryGetInfo(string rootPath, out RecycleBinInfo info)
	{
		info = default;
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		try
		{
			var query = new NativeRecycleBinInfo { Size = Marshal.SizeOf<NativeRecycleBinInfo>() };
			if (QueryRecycleBin(rootPath, ref query) != 0)
			{
				return false;
			}

			info = new RecycleBinInfo(query.ItemCount, query.Bytes);
			return true;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	public static CleanupExecutionResult Empty(string rootPath, CleanupSnapshot snapshot)
	{
		if (!OperatingSystem.IsWindows())
		{
			return new CleanupExecutionResult(0, 0, 1, new[] { "Recycle Bin is only available on Windows." });
		}

		try
		{
			var result = EmptyRecycleBin(IntPtr.Zero, rootPath, EmptyNoConfirmation | EmptyNoProgressUi | EmptyNoSound);
			return result == 0
				? new CleanupExecutionResult((int)Math.Min(snapshot.FileCount, int.MaxValue), snapshot.Bytes, 0, Array.Empty<string>())
				: new CleanupExecutionResult(0, 0, 1, new[] { $"SHEmptyRecycleBin returned HRESULT 0x{result:X8}." });
		}
		catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
		{
			return new CleanupExecutionResult(0, 0, 1, new[] { exception.Message });
		}
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
	private static extern int QueryRecycleBin(string? rootPath, ref NativeRecycleBinInfo recycleBinInfo);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBinW")]
	private static extern int EmptyRecycleBin(IntPtr windowHandle, string? rootPath, uint flags);

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeRecycleBinInfo
	{
		public int Size;
		public long Bytes;
		public long ItemCount;
	}
}
