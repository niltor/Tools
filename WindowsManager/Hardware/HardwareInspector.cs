using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;
using WindowsManager.Localization;

namespace WindowsManager.Hardware;

public sealed class HardwareInspector(Localizer text)
{
	private sealed record Entry(string Name, string Value, bool Highlight = false);
	private sealed record Section(string Name, IReadOnlyList<Entry> Entries);

	public async Task<int> ShowAsync()
	{
		AnsiConsole.Write(new Rule(text.Language == AppLanguage.Chinese ? "硬件检测" : "Hardware Inspection") { Style = Style.Parse("cyan") });
		try
		{
			if (OperatingSystem.IsWindows()) await ShowWindowsAsync();
			else if (OperatingSystem.IsLinux()) await ShowLinuxAsync();
			else if (OperatingSystem.IsMacOS()) await ShowMacAsync();
			else
			{
				AnsiConsole.MarkupLine(text.Language == AppLanguage.Chinese
					? "[yellow]当前系统暂不支持硬件信息采集。[/]" : "[yellow]Hardware inspection is not supported on this system.[/]");
				return 1;
			}
			return 0;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or Win32Exception)
		{
			AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(ex.Message)}[/]");
			return 1;
		}
	}

	private async Task ShowWindowsAsync()
	{
		const string script = """
$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$os = $null
$board = $null
function Out($group, $name, $value) { if ($null -eq $value -or "$value" -eq '') { $value = '不可用' }; Write-Output ($group + '|' + $name + '|' + $value); [Console]::Out.Flush() }
function Done($group) { Write-Output ('DONE|' + $group + '|'); [Console]::Out.Flush() }
Out 'CPU' '型号' $cpu.Name
Out 'CPU' '核心 / 逻辑处理器' ($cpu.NumberOfCores.ToString() + ' / ' + $cpu.NumberOfLogicalProcessors.ToString())
Out 'CPU' '当前负载' $(if ($null -ne $cpu.LoadPercentage) { $cpu.LoadPercentage.ToString() + '%' } else { '不可用' })
Out 'CPU' '最大频率' $(if ($cpu.MaxClockSpeed) { $cpu.MaxClockSpeed.ToString() + ' MHz' } else { '不可用' })
Done 'CPU'
$os = Get-CimInstance Win32_OperatingSystem
$total = [double]$os.TotalVisibleMemorySize * 1KB; $free = [double]$os.FreePhysicalMemory * 1KB; $used = $total - $free
Out '内存' '物理内存' ('{0:N1} GB' -f ($total / 1GB))
Out '内存' '已用 / 使用率' ('{0:N1} GB / {1:N0}%' -f ($used / 1GB), (100 * $used / $total))
foreach ($m in Get-CimInstance Win32_PhysicalMemory) { Out '内存' '内存条' ($m.PartNumber.Trim() + ' · {0:N0} GB · {1} MHz' -f ($m.Capacity / 1GB), $m.Speed) }
Done '内存'
$nvidiaRows = @(& nvidia-smi --query-gpu=name,memory.total,memory.used,utilization.gpu --format=csv,noheader,nounits 2>$null)
foreach ($g in Get-CimInstance Win32_VideoController) {
  Out 'GPU' '型号' $g.Name
  $vram = '{0:N1} GB (系统估算) · {1}' -f ($g.AdapterRAM / 1GB), $g.DriverVersion
  $nvLine = $nvidiaRows | Where-Object { $_.StartsWith($g.Name) } | Select-Object -First 1
  if ($nvLine) { $parts = $nvLine -split ','; $vram = ('{0:N1} GB · {1}' -f ([double]$parts[1].Trim() / 1024), $g.DriverVersion) }
  Out 'GPU' '显存 / 驱动' $vram
  Out 'GPU' '状态' $g.Status
}
foreach ($line in $nvidiaRows) { $parts = $line -split ','; if ($parts.Length -ge 4) { Out 'GPU' 'NVIDIA 实时性能' ('{0} · GPU {1}% · 显存 {2} / {3} MiB' -f $parts[0].Trim(), $parts[3].Trim(), $parts[2].Trim(), $parts[1].Trim()) } }
try { $gpu = (Get-Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction Stop).CounterSamples | Measure-Object -Property CookedValue -Maximum; Out 'GPU' '当前引擎利用率峰值' ('{0:N0}%' -f $gpu.Maximum) } catch { Out 'GPU' '当前引擎利用率' '不可用' }
Done 'GPU'
$board = Get-CimInstance Win32_BaseBoard | Select-Object -First 1
Out '主板' '制造商 / 型号' ($board.Manufacturer + ' · ' + $board.Product)
Out '主板' '版本' $board.Version
Done '主板'
foreach ($d in Get-CimInstance Win32_DiskDrive) { Out '硬盘' '型号' $d.Model; Out '硬盘' '容量 / 类型 / 状态' ('{0:N0} GB · {1} · {2}' -f ($d.Size / 1GB), $d.MediaType, $d.Status) }
foreach ($v in Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3') { Out '硬盘分区' $v.DeviceID ('可用 {0:N1} / {1:N1} GB ({2:N0}%)' -f ($v.FreeSpace / 1GB), ($v.Size / 1GB), (100 * $v.FreeSpace / [math]::Max(1,$v.Size))) }
Done '硬盘'
""";
		var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
		foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
		using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start powershell.exe.");
		var stderr = process.StandardError.ReadToEndAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		string? activeGroup = null;
		var activeEntries = new List<Entry>();
		try
		{
			while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
			{
				var parts = line.Trim().Split('|', 3);
				if (parts.Length != 3) continue;
				if (parts[0] == "DONE")
				{
					if (activeGroup == parts[1])
					{
						WriteSection(new Section(Translate(activeGroup), activeEntries.ToArray()));
						activeGroup = null;
						activeEntries.Clear();
					}
					continue;
				}
				if (activeGroup is not null && activeGroup != parts[0])
				{
					WriteSection(new Section(Translate(activeGroup), activeEntries.ToArray()));
					activeEntries.Clear();
				}
				activeGroup = parts[0];
				activeEntries.Add(new Entry(Translate(parts[1]), parts[2], parts[1] is "型号" or "制造商 / 型号" or "物理内存" or "NVIDIA 实时性能"));
			}
			await process.WaitForExitAsync(timeout.Token);
		}
		catch (OperationCanceledException)
		{
			process.Kill(true);
			throw new TimeoutException("Hardware inspection timed out.");
		}
		_ = await stderr;
		if (activeGroup is not null) WriteSection(new Section(Translate(activeGroup), activeEntries.ToArray()));

		var volumes = new List<Entry>();
		foreach (var drive in DriveInfo.GetDrives())
		{
			try
			{
				if (!drive.IsReady) continue;
				var total = drive.TotalSize;
				var free = drive.AvailableFreeSpace;
				var usedPercent = total > 0 ? 100d * (total - free) / total : 0;
				volumes.Add(new Entry(drive.Name, $"{FormatSize(total - free)} / {FormatSize(total)} used · {usedPercent:N0}%"));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DriveNotFoundException) { }
		}
		WriteSection(new Section(L("磁盘空间", "Disk Space"), volumes.Count == 0 ? [new Entry(L("分区", "Volume"), "")] : volumes));
	}

	private async Task ShowLinuxAsync()
	{
		var cpu = await RunOptionalAsync("lscpu", []);
		var cpuSection = SectionFromLines(L("CPU", "CPU"), cpu, ["Model name", "型号", "CPU max MHz", "CPU MHz"], ["Model name", "CPU(s)", "Core(s) per socket", "Socket(s)", "CPU MHz", "CPU max MHz", "CPU(s) scaling MHz"]);
		var load = (await ReadTextOptionalAsync("/proc/loadavg")).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
		var cpuEntries = cpuSection.Entries.ToList();
		cpuEntries.Add(new Entry(L("1 分钟平均负载", "1-minute load average"), load ?? ""));
		WriteSection(new Section(cpuSection.Name, cpuEntries));
		var mem = await RunOptionalAsync("free", ["-h"]);
		var memSection = SectionFromLines(L("内存", "Memory"), mem, ["Mem", "内存"], ["Mem", "Swap"]);
		var memEntries = memSection.Entries.ToList();
		var memInfo = await ReadTextOptionalAsync("/proc/meminfo");
		var memory = memInfo.Split('\n').Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2)
			.ToDictionary(parts => parts[0], parts => long.TryParse(parts[1].Trim().Split(' ')[0], out var value) ? value : 0, StringComparer.OrdinalIgnoreCase);
		if (memory.TryGetValue("MemTotal", out var totalKb) && totalKb > 0 && memory.TryGetValue("MemAvailable", out var availableKb))
			memEntries.Add(new Entry(L("已用 / 使用率", "Used / utilization"), $"{(totalKb - availableKb) / 1024d:N0} / {totalKb / 1024d:N0} MiB · {100d * (totalKb - availableKb) / totalKb:N0}%"));
		WriteSection(new Section(memSection.Name, memEntries));
		var gpu = await RunOptionalAsync("lspci", ["-mm"]);
		var gpuLines = gpu.Split('\n').Where(line => line.Contains("VGA", StringComparison.OrdinalIgnoreCase) || line.Contains("3D controller", StringComparison.OrdinalIgnoreCase) || line.Contains("Display controller", StringComparison.OrdinalIgnoreCase)).ToArray();
		var gpuEntries = gpuLines.Select((line, index) => new Entry(index == 0 ? L("型号", "Model") : L("设备", "Device"), line.Trim(), true)).ToList();
		var gpuMetrics = await RunOptionalAsync("nvidia-smi", ["--query-gpu=name,utilization.gpu,memory.used,memory.total", "--format=csv,noheader,nounits"]);
		gpuEntries.Add(new Entry(L("当前利用率 / 显存", "Current utilization / VRAM"), gpuMetrics.Trim(), !string.IsNullOrWhiteSpace(gpuMetrics)));
		if (gpuEntries.Count == 1) gpuEntries.Insert(0, new Entry(L("型号", "Model"), ""));
		WriteSection(new Section(L("GPU / 显卡", "GPU / Graphics"), gpuEntries));
		var board = await FileReadAsync("/sys/devices/virtual/dmi/id/board_vendor", "/sys/devices/virtual/dmi/id/board_name");
		WriteSection(new Section(L("主板", "Motherboard"), board));
		var disks = await RunOptionalAsync("lsblk", ["-dn", "-o", "NAME,MODEL,SIZE,ROTA,TRAN"]);
		WriteSection(SectionFromLines(L("硬盘", "Drives"), disks, ["MODEL", "型号"], ["NAME", "MODEL", "SIZE", "ROTA", "TRAN"]));
		var df = await RunOptionalAsync("df", ["-h", "-x", "tmpfs", "-x", "devtmpfs"]);
		WriteSection(SectionFromLines(L("硬盘空间", "Disk Space"), df, ["Filesystem", "文件系统"], ["Filesystem", "Size", "Used", "Avail", "Use%", "Mounted on"]));
	}

	private async Task ShowMacAsync()
	{
		var cpu = await RunOptionalAsync("sysctl", ["-n", "machdep.cpu.brand_string"]);
		var counts = await RunOptionalAsync("sysctl", ["-n", "hw.physicalcpu", "hw.logicalcpu", "hw.memsize"]);
		var cpuEntries = new List<Entry> { new(L("型号", "Model"), cpu.Trim(), true) };
		var values = counts.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		if (values.Length > 0) cpuEntries.Add(new(L("核心数", "Cores"), values[0]));
		if (values.Length > 1) cpuEntries.Add(new(L("逻辑处理器", "Logical CPUs"), values[1]));
		var mem = values.Length > 2 && long.TryParse(values[2], out var bytes) ? $"{bytes / 1024d / 1024 / 1024:N1} GB" : "";
		WriteSection(new Section(L("CPU", "CPU"), cpuEntries));
		WriteSection(new Section(L("内存", "Memory"), [new Entry(L("物理内存", "Physical memory"), mem, true)]));
		var load = await RunOptionalAsync("sysctl", ["-n", "vm.loadavg"]);
		if (!string.IsNullOrWhiteSpace(load)) cpuEntries.Add(new Entry(L("系统平均负载", "System load average"), load.Trim(' ', '{', '}')));
		foreach (var (label, dataType) in new[] { (L("GPU / 显卡", "GPU / Graphics"), "SPDisplaysDataType"), (L("主板与设备", "Board and devices"), "SPHardwareDataType"), (L("硬盘", "Storage"), "SPStorageDataType") })
		{
			var output = await RunOptionalAsync("system_profiler", [dataType, "-detailLevel", "mini"]);
			WriteSection(SectionFromLines(label, output, ["Chipset Model", "型号名称", "Model Name"], []));
		}
		var volumes = await RunOptionalAsync("df", ["-h"]);
		WriteSection(SectionFromLines(L("磁盘空间", "Disk Space"), volumes, [], []));
	}

	private void WriteSection(Section section)
	{
		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
		table.AddColumn(new TableColumn(new Markup($"[bold cyan]{Markup.Escape(section.Name)}[/]")).NoWrap());
		table.AddColumn(L("信息", "Information"));
		foreach (var entry in section.Entries)
		{
			var value = Markup.Escape(string.IsNullOrWhiteSpace(entry.Value) ? L("不可用", "Unavailable") : entry.Value);
			table.AddRow(new IRenderable[] { new Text(entry.Name), entry.Highlight ? new Markup($"[bold yellow]{value}[/]") : new Markup(value) });
		}
		AnsiConsole.Write(table);
		AnsiConsole.WriteLine();
	}

	private Section SectionFromLines(string title, string output, string[] highlightedPrefixes, string[] preferredPrefixes)
	{
		var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd()).Where(line => line.Length > 0).ToArray();
		if (preferredPrefixes.Length > 0) lines = lines.Where(line => preferredPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) || line.StartsWith("/dev/", StringComparison.OrdinalIgnoreCase)).ToArray();
		var entries = lines.Select(line =>
		{
			var split = line.Split(':', 2);
			return split.Length == 2
				? new Entry(split[0].Trim(), split[1].Trim(), highlightedPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
				: new Entry(L("信息", "Info"), line, highlightedPrefixes.Any(prefix => line.Contains(prefix, StringComparison.OrdinalIgnoreCase)));
		}).ToArray();
		return new Section(title, entries.Length == 0 ? [new Entry(L("信息", "Info"), "")] : entries);
	}

	private async Task<IReadOnlyList<Entry>> FileReadAsync(params string[] paths)
	{
		var rows = new List<Entry>();
		foreach (var path in paths)
		{
			try { rows.Add(new Entry(Path.GetFileName(path), (await File.ReadAllTextAsync(path)).Trim(), path.EndsWith("board_name", StringComparison.Ordinal))); }
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
		}
		return rows.Count == 0 ? [new Entry(L("信息", "Info"), "")] : rows;
	}

	private static async Task<string> ReadTextOptionalAsync(string path)
	{
		try { return await File.ReadAllTextAsync(path); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
	}

	private async Task<string> RunOptionalAsync(string file, string[] args)
	{
		try { return await RunAsync(file, args); }
		catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or TimeoutException or InvalidOperationException) { return ""; }
	}

	private static async Task<string> RunAsync(string file, string[] args)
	{
		var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
		foreach (var arg in args) start.ArgumentList.Add(arg);
		using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {file}.");
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		try { await process.WaitForExitAsync(timeout.Token); }
		catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException($"{file} timed out."); }
		var output = await stdout;
		_ = await stderr;
		return output;
	}

	private string L(string chinese, string english) => text.Language == AppLanguage.Chinese ? chinese : english;

	private static string FormatSize(long bytes) => $"{bytes / 1024d / 1024 / 1024:N1} GB";

	private string Translate(string chinese) => text.Language == AppLanguage.Chinese ? chinese : chinese switch
	{
		"CPU" => "CPU",
		"内存" => "Memory",
		"GPU" => "GPU / Graphics",
		"主板" => "Motherboard",
		"硬盘" => "Drives",
		"硬盘分区" => "Volumes",
		"型号" => "Model",
		"核心 / 逻辑处理器" => "Cores / logical processors",
		"当前负载" => "Current load",
		"最大频率" => "Maximum frequency",
		"物理内存" => "Physical memory",
		"已用 / 使用率" => "Used / utilization",
		"内存条" => "Memory module",
		"显存 / 驱动" => "VRAM / driver",
		"显存 / 驱动(系统报告)" => "VRAM / driver (system reported)",
		"状态" => "Status",
		"NVIDIA 实时性能" => "NVIDIA live metrics",
		"当前引擎利用率峰值" => "Peak engine utilization",
		"当前引擎利用率" => "Engine utilization",
		"制造商 / 型号" => "Manufacturer / model",
		"版本" => "Version",
		"容量 / 类型 / 状态" => "Capacity / type / status",
		_ => chinese
	};
}
