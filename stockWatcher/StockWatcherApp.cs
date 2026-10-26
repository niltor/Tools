using System.CommandLine;
using System.Globalization;
using StockWatcher.Models;
using StockWatcher.Services;

namespace StockWatcher;

internal sealed class StockWatcherApp(YahooFinanceClient yahoo, WatchlistStore watchlist, SettingsStore settingsStore)
{
    public RootCommand BuildCommand()
    {
        var root = new RootCommand("搜索 A 股和美股、管理自选并查看当日行情。");

        var searchCommand = new Command("search", "搜索全部支持的市场，选择加入自选或按代码更新已有信息。");
        var searchQuery = new Argument<string>("query") { Description = "股票代码或名称。" };
        searchCommand.Arguments.Add(searchQuery);
        searchCommand.SetAction(async (parseResult, cancellationToken) => await RunSafelyAsync(
            () => SearchAndAddAsync(
                parseResult.GetValue(searchQuery) ?? string.Empty,
                cancellationToken)));
        root.Subcommands.Add(searchCommand);

        var listCommand = new Command("list", "显示自选股及当前行情。");
        listCommand.SetAction(async (parseResult, cancellationToken) => await RunSafelyAsync(
            () => ListAsync(cancellationToken)));
        root.Subcommands.Add(listCommand);

        var configCommand = new Command("config", "打开配置菜单并保存设置。");
        configCommand.SetAction(async (parseResult, cancellationToken) => await RunSafelyAsync(
            () => ConfigureAsync(cancellationToken)));
        root.Subcommands.Add(configCommand);

        var showCommand = new Command("show", "按配置间隔刷新自选股当日行情，按 Ctrl+C 停止。");
        var intervalOption = new Option<int?>("--interval")
        {
            Description = "本次行情刷新间隔（1～10 秒）；未指定时使用 config 保存的设置，初始为 3 秒。"
        };
        intervalOption.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<int?>();
            if (value is < AppSettings.MinRefreshIntervalSeconds or > AppSettings.MaxRefreshIntervalSeconds)
            {
                result.AddError("--interval 必须是 1 到 10 之间的秒数。");
            }
        });
        showCommand.Options.Add(intervalOption);
        var noMaskOption = new Option<bool>("--no-mask")
        {
            Description = "关闭默认灰色背景和 Alt 切换效果。"
        };
        showCommand.Options.Add(noMaskOption);
        showCommand.SetAction(async (parseResult, cancellationToken) => await RunSafelyAsync(
            () => ShowAsync(parseResult.GetValue(intervalOption), parseResult.GetValue(noMaskOption), cancellationToken)));
        root.Subcommands.Add(showCommand);

        var removeSymbol = new Argument<string>("symbol") { Description = "自选中的 Yahoo Finance 股票代码。" };
        var removeCommand = new Command("remove", "从自选中删除股票。");
        removeCommand.Arguments.Add(removeSymbol);
        removeCommand.SetAction(async (parseResult, cancellationToken) => await RunSafelyAsync(
            () => RemoveAsync(parseResult.GetValue(removeSymbol) ?? string.Empty, cancellationToken)));
        root.Subcommands.Add(removeCommand);

        return root;
    }

    private async Task<int> SearchAndAddAsync(string query, CancellationToken cancellationToken)
    {
        var results = await yahoo.SearchStocksAsync(query.Trim(), "all", cancellationToken);
        if (results.Count == 0)
        {
            Console.WriteLine("没有找到符合条件的 A 股或美股。可尝试股票代码或英文公司名。");
            return 0;
        }

        PrintSearchResults(results);
        if (Console.IsInputRedirected)
        {
            Console.WriteLine("请在交互终端运行 stockWatcher search <代码或名称>，选择结果以添加或更新自选。");
            return 0;
        }

        Console.Write($"请输入要添加或更新的序号（1-{results.Count}，直接回车取消）：");
        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            Console.WriteLine("已取消。");
            return 0;
        }

        if (!int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var selected)
            || selected < 1
            || selected > results.Count)
        {
            Console.Error.WriteLine("序号无效，没有添加股票。");
            return 1;
        }

        return await AddSelectedAsync(results[selected - 1], cancellationToken);
    }

    private async Task<int> AddSelectedAsync(SavedStock stock, CancellationToken cancellationToken)
    {
        var stocks = await watchlist.LoadAsync(cancellationToken);
        var existingIndex = stocks.FindIndex(item => string.Equals(item.Symbol, stock.Symbol, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            if (stocks[existingIndex] != stock)
            {
                stocks[existingIndex] = stock;
                await watchlist.SaveAsync(stocks, cancellationToken);
                Console.WriteLine($"已更新自选信息：{stock.Symbol}  {stock.Name}");
            }
            else
            {
                Console.WriteLine($"{stock.Symbol} 已在自选中。");
            }

            return 0;
        }

        stocks.Add(stock);
        await watchlist.SaveAsync(stocks, cancellationToken);
        Console.WriteLine($"已加入自选：{stock.Symbol}  {stock.Name}");
        Console.WriteLine($"自选文件：{watchlist.FilePath}");
        return 0;
    }

    private async Task<int> ListAsync(CancellationToken cancellationToken)
    {
        var stocks = await watchlist.LoadAsync(cancellationToken);
        if (stocks.Count == 0)
        {
            Console.WriteLine("自选列表为空。运行 stockWatcher search <代码或名称> 添加股票。");
            return 0;
        }

        var quotes = await yahoo.GetQuotesAsync(stocks, cancellationToken);
        PrintQuotes(quotes, DateTimeOffset.Now, clearScreen: false);
        return quotes.Any(static quote => quote.Error is not null) ? 1 : 0;
    }

    private async Task<int> ConfigureAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine("配置");
            Console.WriteLine($"1. 行情刷新(s)：{settings.RefreshIntervalSeconds}（1～10）");
            Console.WriteLine($"2. 默认灰色背景：{(settings.MaskBackground ? "开启" : "关闭")}");
            Console.WriteLine("0. 返回");
            Console.Write("请选择（回车返回）：");
            var choice = (await Console.In.ReadLineAsync(cancellationToken))?.Trim();
            if (string.IsNullOrEmpty(choice) || choice == "0")
            {
                return 0;
            }

            AppSettings updated;
            if (choice == "1")
            {
                Console.Write("行情刷新(s)，请输入 1～10（回车取消）：");
                var input = (await Console.In.ReadLineAsync(cancellationToken))?.Trim();
                if (input is null)
                {
                    return 0;
                }

                if (input.Length == 0)
                {
                    continue;
                }

                if (!int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                    || seconds is < AppSettings.MinRefreshIntervalSeconds or > AppSettings.MaxRefreshIntervalSeconds)
                {
                    Console.Error.WriteLine("行情刷新间隔必须是 1～10 之间的整数。");
                    continue;
                }

                updated = settings with { RefreshIntervalSeconds = seconds };
            }
            else if (choice == "2")
            {
                updated = settings with { MaskBackground = !settings.MaskBackground };
            }
            else
            {
                Console.Error.WriteLine("请输入 0、1 或 2。");
                continue;
            }

            await settingsStore.SaveAsync(updated, cancellationToken);
            settings = updated;
            Console.WriteLine("已保存，下次运行 show 时生效。");
            Console.WriteLine();
        }
    }

    private async Task<int> ShowAsync(int? intervalSeconds, bool noMask, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var refreshInterval = intervalSeconds ?? settings.RefreshIntervalSeconds;
        var stocks = await watchlist.LoadAsync(cancellationToken);
        if (stocks.Count == 0)
        {
            Console.WriteLine("自选列表为空。运行 stockWatcher search <代码或名称> 添加股票。");
            return 0;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            stop.Cancel();
        }

        Console.CancelKeyPress += OnCancel;
        try
        {
            await using var display = LiveQuoteDisplay.Start(noMask || !settings.MaskBackground, stop.Token);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(refreshInterval));
            do
            {
                var quotes = await yahoo.GetQuotesAsync(stocks, stop.Token);
                display.Update(quotes, DateTimeOffset.Now);
            }
            while (await timer.WaitForNextTickAsync(stop.Token));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Ctrl+C exits silently after the display restores its original style.
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }

        return 0;
    }

    private async Task<int> RemoveAsync(string symbol, CancellationToken cancellationToken)
    {
        var stocks = await watchlist.LoadAsync(cancellationToken);
        var removed = stocks.RemoveAll(item => string.Equals(item.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            Console.Error.WriteLine($"自选中没有找到代码 {symbol}。");
            return 1;
        }

        await watchlist.SaveAsync(stocks, cancellationToken);
        Console.WriteLine($"已从自选中删除：{symbol}");
        return 0;
    }

    private static void PrintSearchResults(IReadOnlyList<SavedStock> results)
    {
        Console.WriteLine($"找到 {results.Count} 只股票：");
        for (var index = 0; index < results.Count; index++)
        {
            var stock = results[index];
            Console.WriteLine($"{index + 1,2}. [{stock.Market}] {stock.Symbol,-14} {stock.Name} ({stock.Exchange})");
        }
    }

    private static void PrintQuotes(IReadOnlyList<StockQuote> quotes, DateTimeOffset updatedAt, bool clearScreen)
    {
        if (clearScreen && !Console.IsOutputRedirected)
        {
            try
            {
                Console.Clear();
            }
            catch (IOException)
            {
            }
        }

        Console.WriteLine($"自选行情  更新于 {updatedAt:yyyy-MM-dd HH:mm:ss zzz}  (Yahoo Finance)");
        Console.WriteLine(new string('-', 108));
        Console.WriteLine($"{"市场",-6} {"代码",-14} {"名称",-28} {"现价",12} {"涨跌幅",10} {"日内高/低",25} {"成交量",12}");

        foreach (var quote in quotes)
        {
            if (quote.Error is not null)
            {
                Console.WriteLine($"{quote.Stock.Market,-6} {quote.Stock.Symbol,-14} {Trim(quote.Stock.Name, 28),-28} {"--",12} {"--",10} {Trim(quote.Error, 25),25} {"--",12}");
                continue;
            }

            var currency = quote.Currency is null ? string.Empty : $" {quote.Currency}";
            var price = quote.Price.HasValue ? quote.Price.Value.ToString("N2", CultureInfo.InvariantCulture) + currency : "--";
            var change = quote.ChangePercent.HasValue ? $"{quote.ChangePercent.Value:+0.00;-0.00;0.00}%" : "--";
            var dayRange = quote.DayHigh.HasValue || quote.DayLow.HasValue
                ? $"{quote.DayHigh?.ToString("N2", CultureInfo.InvariantCulture) ?? "--"} / {quote.DayLow?.ToString("N2", CultureInfo.InvariantCulture) ?? "--"}"
                : "--";
            var volume = quote.Volume?.ToString("N0", CultureInfo.InvariantCulture) ?? "--";
            Console.WriteLine($"{quote.Stock.Market,-6} {quote.Stock.Symbol,-14} {Trim(quote.Stock.Name, 28),-28} {price,12} {change,10} {dayRange,25} {volume,12}");
        }

        Console.WriteLine(new string('-', 108));
        Console.WriteLine("数据可能延迟；按 Ctrl+C 停止刷新。");
    }

    private static string Trim(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    private static async Task<int> RunSafelyAsync(Func<Task<int>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (YahooFinanceException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"网络请求失败：{ex.Message}");
            return 1;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"读写本地数据失败：{ex.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"没有权限读写本地数据：{ex.Message}");
            return 1;
        }
        catch (System.Text.Json.JsonException ex)
        {
            Console.Error.WriteLine($"自选文件格式错误：{ex.Message}");
            return 1;
        }
    }
}
