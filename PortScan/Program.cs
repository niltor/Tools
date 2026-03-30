using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

var options = ScanOptions.Parse(args);
if (options is null)
{
	ScanOptions.WriteUsage();
	return;
}

Console.WriteLine($"Target  : {options.TargetHost}");
Console.WriteLine($"Range   : {options.StartPort}-{options.EndPort}");
Console.WriteLine($"Timeout : {options.Timeout.TotalSeconds:0.##}s");
Console.WriteLine($"Threads : {options.MaxParallelism}");
Console.WriteLine();

var scanner = new PortScanner(options.TargetHost, options.StartPort, options.EndPort, options.Timeout, options.MaxParallelism);
var totalPorts = options.EndPort - options.StartPort + 1;
var scannedCount = 0;
var openCount = 0;
var consoleLock = new object();
var progressLineLength = 0;
using var progressCts = new CancellationTokenSource();
var stopwatch = Stopwatch.StartNew();

void WriteLineLocked(string message)
{
	lock (consoleLock)
	{
		if (progressLineLength > 0)
		{
			Console.Write('\r');
			Console.Write(new string(' ', progressLineLength));
			Console.Write('\r');
			progressLineLength = 0;
		}

		Console.WriteLine(message);
	}
}

void UpdateProgressLine()
{
	lock (consoleLock)
	{
		var message = $"Progress: {scannedCount}/{totalPorts} scanned, {openCount} open, elapsed {stopwatch.Elapsed.TotalSeconds:0.0}s";
		progressLineLength = Math.Max(progressLineLength, message.Length);
		Console.Write('\r');
		Console.Write(message.PadRight(progressLineLength));
	}
}

var progressTask = Task.Run(async () =>
{
	using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
	while (await timer.WaitForNextTickAsync(progressCts.Token).ConfigureAwait(false))
	{
		UpdateProgressLine();
	}
}, progressCts.Token);

var results = await scanner.ScanAsync(
	onPortScanned: _ => Interlocked.Increment(ref scannedCount),
	onOpenPort: result =>
	{
		Interlocked.Increment(ref openCount);
		WriteLineLocked(result.Format());
	});

progressCts.Cancel();
try
{
	await progressTask;
}
catch (OperationCanceledException)
{
}

WriteLineLocked(string.Empty);

var elapsed = stopwatch.Elapsed;

if (results.Count == 0)
{
	Console.WriteLine("No open ports found.");
	Console.WriteLine($"Elapsed: {elapsed.TotalSeconds:0.00}s");
	return;
}

Console.WriteLine();
Console.WriteLine($"Open count: {results.Count}");
Console.WriteLine($"Elapsed   : {elapsed.TotalSeconds:0.00}s");

internal sealed record ScanOptions(string TargetHost, int StartPort, int EndPort, TimeSpan Timeout, int MaxParallelism)
{
	private const int DefaultThreads = 20;
	private const int MinThreads = 10;
	private const int MaxThreads = 100;

	public static ScanOptions? Parse(string[] args)
	{
		if (args.Length is 0 or > 5)
		{
			return null;
		}

		if (args[0] is "-h" or "--help" or "/?")
		{
			return null;
		}

		var host = args[0].Trim();
		if (string.IsNullOrWhiteSpace(host))
		{
			return null;
		}

		var startPort = 1;
		var endPort = 65_535;

		if (args.Length >= 2 && (!TryParsePort(args[1], out startPort) || startPort < 1 || startPort > 65_535))
		{
			return null;
		}

		if (args.Length >= 3 && (!TryParsePort(args[2], out endPort) || endPort < 1 || endPort > 65_535))
		{
			return null;
		}

		var timeoutSeconds = 0.3d;
		if (args.Length >= 4 && (!double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out timeoutSeconds) || timeoutSeconds <= 0))
		{
			return null;
		}

		var maxParallelism = DefaultThreads;
		if (args.Length >= 5 && int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedThreads) && parsedThreads >= MinThreads && parsedThreads <= MaxThreads)
		{
			maxParallelism = parsedThreads;
		}

		if (startPort > endPort)
		{
			return null;
		}

		return new ScanOptions(host, startPort, endPort, TimeSpan.FromSeconds(timeoutSeconds), maxParallelism);
	}

	public static void WriteUsage()
	{
		Console.WriteLine("PortScan - scan open TCP ports and detect web services.");
		Console.WriteLine();
		Console.WriteLine("Usage:");
		Console.WriteLine("  PortScan <host> [startPort] [endPort] [timeoutSeconds] [threads]");
		Console.WriteLine();
		Console.WriteLine("Examples:");
		Console.WriteLine("  PortScan 192.168.1.10");
		Console.WriteLine("  PortScan 192.168.1.10 1 1024");
		Console.WriteLine("  PortScan 192.168.1.10 1 1024 0.5");
		Console.WriteLine("  PortScan 192.168.1.10 1 1024 0.5 50");
		Console.WriteLine();
		Console.WriteLine("Notes:");
		Console.WriteLine("  - Multithreaded with 20 workers by default.");
		Console.WriteLine("  - Thread count is only accepted when it is an integer from 10 to 100; otherwise the default 20 is used.");
		Console.WriteLine("  - Each port probe times out after 0.3 seconds by default.");
		Console.WriteLine("  - You can override timeout with the optional 4th argument (seconds).");
		Console.WriteLine("  - Open ports are further checked for HTTP/HTTPS responses.");
	}

	private static bool TryParsePort(string value, out int port)
		=> int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
}
