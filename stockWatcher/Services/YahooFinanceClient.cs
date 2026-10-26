using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using StockWatcher.Models;

namespace StockWatcher.Services;

internal sealed class YahooFinanceClient : IDisposable
{
    private const string SearchEndpoint = "https://query1.finance.yahoo.com/v1/finance/search";
    private const string SparkEndpoint = "https://query1.finance.yahoo.com/v7/finance/spark";
    private const string EastmoneySearchEndpoint = "https://searchapi.eastmoney.com/api/suggest/get";
    private static readonly HashSet<string> UsMajorYahooExchanges = new(StringComparer.OrdinalIgnoreCase)
    {
        "NMS", "NGM", "NCM", "NAS", "NASDAQ", "NYQ", "NYSE", "ASE", "AMEX", "PCX", "BTS", "BATS"
    };
    private static readonly HashSet<string> UsMajorEastmoneyExchanges = new(StringComparer.OrdinalIgnoreCase)
    {
        "NASDAQ", "NYSE", "AMEX", "NYSE American"
    };

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _openingPriceConcurrency = new(8);
    private readonly ConcurrentDictionary<string, (long TradingDay, decimal Price)> _openingPrices = new(StringComparer.OrdinalIgnoreCase);

    public YahooFinanceClient()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("stockWatcher/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<IReadOnlyList<SavedStock>> SearchStocksAsync(
        string query,
        string market,
        CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return [];
        }

        if (ContainsChineseCharacters(query))
        {
            return await SearchEastmoneyAsync(query, market, cancellationToken);
        }

        if (!string.Equals(market, "us", StringComparison.OrdinalIgnoreCase)
            && IsMainlandCodeQuery(query))
        {
            try
            {
                var localResults = await SearchEastmoneyAsync(query[..6], "cn", cancellationToken);
                var matchingResults = localResults
                    .Where(stock => query.Length == 6
                        ? stock.Symbol.StartsWith(query + ".", StringComparison.OrdinalIgnoreCase)
                        : string.Equals(stock.Symbol, query, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matchingResults.Length > 0)
                {
                    return matchingResults;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A localized-name lookup timeout must not prevent the Yahoo search.
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or YahooFinanceException)
            {
                // Keep Yahoo search available when the optional name lookup fails.
            }
        }

        var regions = market.ToLowerInvariant() switch
        {
            "cn" => new[] { "CN" },
            "us" => new[] { "US" },
            _ => new[] { "CN", "US" }
        };

        var requests = regions.Select(region => SearchRegionAsync(query, region, cancellationToken));
        var results = await Task.WhenAll(requests);

        var stocksFound = results
            .SelectMany(static items => items)
            .Where(IsSupportedYahooEquity)
            .Select(ToYahooSavedStock)
            .DistinctBy(static stock => stock.Symbol, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToArray();

        return stocksFound.Length > 0
            ? stocksFound
            : await SearchEastmoneyAsync(query, market, cancellationToken);
    }

    public async Task<IReadOnlyList<StockQuote>> GetQuotesAsync(
        IReadOnlyList<SavedStock> stocks,
        CancellationToken cancellationToken)
    {
        if (stocks.Count == 0)
        {
            return [];
        }

        var yahooStocks = stocks
            .Where(static stock => !stock.Symbol.EndsWith(".BJ", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var beijingStocks = stocks
            .Where(static stock => stock.Symbol.EndsWith(".BJ", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var yahooTasks = yahooStocks
            .Chunk(20)
            .Select(batch => FetchSparkBatchAsync(batch, cancellationToken))
            .ToArray();
        var beijingTasks = beijingStocks
            .Select(stock => FetchEastmoneyQuoteAsync(stock, cancellationToken))
            .ToArray();
        var yahooResultsTask = Task.WhenAll(yahooTasks);
        var beijingResultsTask = Task.WhenAll(beijingTasks);
        await Task.WhenAll(yahooResultsTask, beijingResultsTask);

        var quoteMap = yahooResultsTask.Result
            .SelectMany(static quotes => quotes.Values)
            .ToDictionary(static quote => quote.Stock.Symbol, StringComparer.OrdinalIgnoreCase);
        foreach (var quote in beijingResultsTask.Result)
        {
            quoteMap[quote.Stock.Symbol] = quote;
        }

        return stocks
            .Select(stock => quoteMap.TryGetValue(stock.Symbol, out var quote)
                ? quote
                : FailedQuote(stock, "Yahoo Finance 没有返回该股票行情。"))
            .ToArray();
    }

    private async Task<IReadOnlyList<YahooSearchResult>> SearchRegionAsync(
        string query,
        string region,
        CancellationToken cancellationToken)
    {
        var uri = $"{SearchEndpoint}?q={Uri.EscapeDataString(query)}&quotesCount=40&newsCount=0&region={region}";
        var response = await GetJsonAsync<YahooSearchResponse>(uri, "股票搜索", cancellationToken);
        return response.Quotes ?? [];
    }

    private async Task<IReadOnlyList<SavedStock>> SearchEastmoneyAsync(
        string query,
        string market,
        CancellationToken cancellationToken)
    {
        var uri = $"{EastmoneySearchEndpoint}?input={Uri.EscapeDataString(query)}&type=14&count=30";
        var response = await GetJsonAsync<EastmoneySearchResponse>(uri, "中文股票名称搜索", cancellationToken);
        var results = response.QuotationCodeTable?.Data ?? [];

        return results
            .Select(result => ToEastmoneySavedStock(result, market))
            .Where(static stock => stock is not null)
            .Select(static stock => stock!)
            .DistinctBy(static stock => stock.Symbol, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToArray();
    }

    private async Task<StockQuote> FetchEastmoneyQuoteAsync(SavedStock stock, CancellationToken cancellationToken)
    {
        try
        {
            var code = Uri.EscapeDataString(stock.Symbol[..^3]);
            var uri = $"https://push2.eastmoney.com/api/qt/stock/get?secid=0.{code}&fields=f43,f44,f45,f46,f47,f60";
            var response = await GetJsonAsync<EastmoneyQuoteResponse>(uri, $"读取 {stock.Symbol} 北交所行情", cancellationToken);
            var data = response.Data;
            if (data?.Price is null || data.PreviousClose is null)
            {
                return FailedQuote(stock, "东方财富没有返回该北交所股票行情。");
            }

            var price = data.Price.Value / 100m;
            var previousClose = data.PreviousClose.Value / 100m;
            var changePercent = previousClose > 0
                ? (price / previousClose - 1m) * 100m
                : (decimal?)null;

            return new StockQuote(
                stock,
                price,
                ScaleEastmoneyPrice(data.OpenPrice),
                previousClose,
                changePercent,
                ScaleEastmoneyPrice(data.DayHigh),
                ScaleEastmoneyPrice(data.DayLow),
                data.Volume,
                "CNY",
                null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FailedQuote(stock, "请求超时");
        }
        catch (HttpRequestException ex)
        {
            return FailedQuote(stock, ex.Message);
        }
        catch (JsonException ex)
        {
            return FailedQuote(stock, $"行情数据格式错误：{ex.Message}");
        }
        catch (YahooFinanceException ex)
        {
            return FailedQuote(stock, ex.Message);
        }
    }

    private async Task<IReadOnlyDictionary<string, StockQuote>> FetchSparkBatchAsync(
        IReadOnlyList<SavedStock> stocks,
        CancellationToken cancellationToken)
    {
        try
        {
            var symbols = string.Join(",", stocks.Select(static stock => Uri.EscapeDataString(stock.Symbol)));
            var uri = $"{SparkEndpoint}?symbols={symbols}&range=1d&interval=1m";
            var response = await GetJsonAsync<YahooSparkResponse>(uri, "批量读取行情", cancellationToken);
            var quotes = await Task.WhenAll(stocks.Select(async item =>
            {
                var sparkResult = response.Spark?.Result?.FirstOrDefault(entry =>
                    string.Equals(entry.Symbol, item.Symbol, StringComparison.OrdinalIgnoreCase));
                var meta = sparkResult?.Response?.FirstOrDefault()?.Meta;
                return meta is null
                    ? FailedQuote(item, "Yahoo Finance 没有返回该股票行情。")
                    : ToStockQuote(item, meta, await GetOpeningPriceAsync(item.Symbol, meta, cancellationToken));
            }));

            return quotes.ToDictionary(static quote => quote.Stock.Symbol, StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FailedQuotes(stocks, "请求超时");
        }
        catch (HttpRequestException ex)
        {
            return FailedQuotes(stocks, ex.Message);
        }
        catch (JsonException ex)
        {
            return FailedQuotes(stocks, $"行情数据格式错误：{ex.Message}");
        }
        catch (YahooFinanceException ex)
        {
            return FailedQuotes(stocks, ex.Message);
        }
    }

    private async Task<decimal?> GetOpeningPriceAsync(string symbol, YahooChartMeta meta, CancellationToken cancellationToken)
    {
        var tradingDay = meta.RegularMarketTime.HasValue && meta.Gmtoffset.HasValue
            ? (meta.RegularMarketTime.Value + meta.Gmtoffset.Value) / 86_400
            : (long?)null;
        if (tradingDay.HasValue && _openingPrices.TryGetValue(symbol, out var cached) && cached.TradingDay == tradingDay)
        {
            return cached.Price;
        }

        await _openingPriceConcurrency.WaitAsync(cancellationToken);
        try
        {
            var uri = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range=1d&interval=1d&includePrePost=false";
            var response = await GetJsonAsync<YahooChartResponse>(uri, $"读取 {symbol} 开盘价", cancellationToken);
            var chart = response.Chart?.Result?.FirstOrDefault();
            var open = chart?.Indicators?.Quote?.FirstOrDefault()?.Open?.FirstOrDefault(static price => price is > 0);
            var timestamp = chart?.Timestamp?.FirstOrDefault();
            if (tradingDay.HasValue && timestamp.HasValue && chart?.Meta?.Gmtoffset is int offset)
            {
                if ((timestamp.Value + offset) / 86_400 != tradingDay.Value)
                {
                    return null;
                }

                if (open.HasValue)
                {
                    _openingPrices[symbol] = (tradingDay.Value, open.Value);
                }
            }

            return open;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or YahooFinanceException)
        {
            return null;
        }
        finally
        {
            _openingPriceConcurrency.Release();
        }
    }

    private async Task<T> GetJsonAsync<T>(string uri, string operation, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = ExtractYahooError(body);
            var message = detail is null
                ? $"{operation}失败（HTTP {(int)response.StatusCode} {response.StatusCode}）。"
                : $"{operation}失败（HTTP {(int)response.StatusCode}）：{detail}";
            throw new YahooFinanceException(message);
        }

        return JsonSerializer.Deserialize(body, GetJsonTypeInfo<T>())
            ?? throw new YahooFinanceException($"{operation}返回了空数据。");
    }

    private static JsonTypeInfo<T> GetJsonTypeInfo<T>() where T : class
    {
        if (typeof(T) == typeof(YahooChartResponse))
        {
            return (JsonTypeInfo<T>)(object)StockWatcherJsonContext.Default.YahooChartResponse;
        }

        if (typeof(T) == typeof(YahooSearchResponse))
        {
            return (JsonTypeInfo<T>)(object)StockWatcherJsonContext.Default.YahooSearchResponse;
        }

        if (typeof(T) == typeof(YahooSparkResponse))
        {
            return (JsonTypeInfo<T>)(object)StockWatcherJsonContext.Default.YahooSparkResponse;
        }

        if (typeof(T) == typeof(EastmoneySearchResponse))
        {
            return (JsonTypeInfo<T>)(object)StockWatcherJsonContext.Default.EastmoneySearchResponse;
        }

        if (typeof(T) == typeof(EastmoneyQuoteResponse))
        {
            return (JsonTypeInfo<T>)(object)StockWatcherJsonContext.Default.EastmoneyQuoteResponse;
        }

        throw new InvalidOperationException($"没有为 {typeof(T).Name} 注册 JSON source metadata。");
    }

    private static string? ExtractYahooError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("chart", out var chart)
                && chart.TryGetProperty("error", out var chartError)
                && chartError.ValueKind == JsonValueKind.Object
                && chartError.TryGetProperty("description", out var chartDescription))
            {
                return chartDescription.GetString();
            }

            if (document.RootElement.TryGetProperty("finance", out var finance)
                && finance.TryGetProperty("error", out var financeError)
                && financeError.ValueKind == JsonValueKind.Object
                && financeError.TryGetProperty("description", out var financeDescription))
            {
                return financeDescription.GetString();
            }

            if (document.RootElement.TryGetProperty("spark", out var spark)
                && spark.TryGetProperty("error", out var sparkError)
                && sparkError.ValueKind == JsonValueKind.Object
                && sparkError.TryGetProperty("description", out var sparkDescription))
            {
                return sparkDescription.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static bool IsSupportedYahooEquity(YahooSearchResult result)
    {
        if (!string.Equals(result.QuoteType, "EQUITY", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(result.Symbol))
        {
            return false;
        }

        var symbol = result.Symbol;
        if (symbol.EndsWith(".SS", StringComparison.OrdinalIgnoreCase)
            || symbol.EndsWith(".SZ", StringComparison.OrdinalIgnoreCase)
            || symbol.EndsWith(".BJ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return result.Exchange is not null && UsMajorYahooExchanges.Contains(result.Exchange);
    }

    private static SavedStock ToYahooSavedStock(YahooSearchResult result)
    {
        var symbol = result.Symbol!;
        var isChineseMarket = symbol.EndsWith(".SS", StringComparison.OrdinalIgnoreCase)
            || symbol.EndsWith(".SZ", StringComparison.OrdinalIgnoreCase)
            || symbol.EndsWith(".BJ", StringComparison.OrdinalIgnoreCase);

        return new SavedStock(
            symbol,
            !string.IsNullOrWhiteSpace(result.LongName) ? result.LongName
                : !string.IsNullOrWhiteSpace(result.ShortName) ? result.ShortName : symbol,
            result.ExchDisp ?? result.Exchange ?? (isChineseMarket ? "中国大陆" : "美国"),
            isChineseMarket ? "A股" : "美股");
    }

    private static SavedStock? ToEastmoneySavedStock(EastmoneySearchResult result, string market)
    {
        if (string.IsNullOrWhiteSpace(result.Code) || string.IsNullOrWhiteSpace(result.Name))
        {
            return null;
        }

        if (result.SecurityTypeName is "沪A" or "深A" or "京A")
        {
            if (string.Equals(market, "us", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var suffix = result.SecurityTypeName switch
            {
                "沪A" => ".SS",
                "深A" => ".SZ",
                "京A" => ".BJ",
                _ => null
            };
            return suffix is null
                ? null
                : new SavedStock(result.Code + suffix, result.Name, result.SecurityTypeName!, "A股");
        }

        if (string.Equals(result.Classify, "UsStock", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(market, "cn", StringComparison.OrdinalIgnoreCase)
            && result.Exchange is not null
            && UsMajorEastmoneyExchanges.Contains(result.Exchange))
        {
            return new SavedStock(result.Code, result.Name, result.Exchange, "美股");
        }

        return null;
    }

    private static bool ContainsChineseCharacters(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value is >= 0x3400 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMainlandCodeQuery(string query)
        => (query.Length == 6 || (query.Length == 9
            && query[6..].ToUpperInvariant() is ".SS" or ".SZ" or ".BJ"))
            && query[..6].All(char.IsAsciiDigit);

    private static StockQuote ToStockQuote(SavedStock stock, YahooChartMeta meta, decimal? openPrice)
    {
        var previousClose = meta.ChartPreviousClose ?? meta.RegularMarketPreviousClose;
        var price = meta.RegularMarketPrice;
        decimal? changePercent = price.HasValue && previousClose is > 0
            ? (price.Value / previousClose.Value - 1m) * 100m
            : null;

        return new StockQuote(
            stock,
            price,
            openPrice,
            previousClose,
            changePercent,
            meta.RegularMarketDayHigh,
            meta.RegularMarketDayLow,
            meta.RegularMarketVolume,
            meta.Currency,
            null);
    }

    private static decimal? ScaleEastmoneyPrice(long? value)
        => value.HasValue ? value.Value / 100m : null;

    private static IReadOnlyDictionary<string, StockQuote> FailedQuotes(IReadOnlyList<SavedStock> stocks, string error)
        => stocks.ToDictionary(
            static stock => stock.Symbol,
            stock => FailedQuote(stock, error),
            StringComparer.OrdinalIgnoreCase);

    private static StockQuote FailedQuote(SavedStock stock, string error)
        => new(stock, null, null, null, null, null, null, null, null, error);

    public void Dispose()
    {
        _httpClient.Dispose();
        _openingPriceConcurrency.Dispose();
    }
}

internal sealed class YahooFinanceException(string message) : Exception(message);
