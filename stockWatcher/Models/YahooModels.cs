using System.Text.Json.Serialization;

namespace StockWatcher.Models;

internal sealed class YahooSearchResponse
{
    public YahooSearchResult[]? Quotes { get; init; }
}

internal sealed class YahooSearchResult
{
    public string? Symbol { get; init; }
    [JsonPropertyName("shortname")]
    public string? ShortName { get; init; }
    [JsonPropertyName("longname")]
    public string? LongName { get; init; }
    public string? Exchange { get; init; }
    public string? ExchDisp { get; init; }
    public string? QuoteType { get; init; }
}

internal sealed class YahooChartResponse
{
    public YahooChartEnvelope? Chart { get; init; }
}

internal sealed class YahooSparkResponse
{
    public YahooSparkEnvelope? Spark { get; init; }
}

internal sealed class YahooSparkEnvelope
{
    public YahooSparkResult[]? Result { get; init; }
    public YahooApiError? Error { get; init; }
}

internal sealed class YahooSparkResult
{
    public string? Symbol { get; init; }
    public YahooSparkQuote[]? Response { get; init; }
}

internal sealed class YahooSparkQuote
{
    public YahooChartMeta? Meta { get; init; }
}

internal sealed class YahooChartEnvelope
{
    public YahooChartResult[]? Result { get; init; }
    public YahooApiError? Error { get; init; }
}

internal sealed class YahooChartResult
{
    public YahooChartMeta? Meta { get; init; }
    public long[]? Timestamp { get; init; }
    public YahooIndicators? Indicators { get; init; }
}

internal sealed class YahooChartMeta
{
    public string? Symbol { get; init; }
    public string? Currency { get; init; }
    public string? ExchangeName { get; init; }
    public string? ShortName { get; init; }
    public string? LongName { get; init; }
    public decimal? RegularMarketPrice { get; init; }
    public long? RegularMarketTime { get; init; }
    public int? Gmtoffset { get; init; }
    public decimal? ChartPreviousClose { get; init; }
    public decimal? RegularMarketPreviousClose { get; init; }
    public decimal? RegularMarketDayHigh { get; init; }
    public decimal? RegularMarketDayLow { get; init; }
    public long? RegularMarketVolume { get; init; }
}

internal sealed class YahooIndicators
{
    public YahooQuoteIndicators[]? Quote { get; init; }
}

internal sealed class YahooQuoteIndicators
{
    public decimal?[]? Open { get; init; }
    public decimal?[]? Close { get; init; }
}

internal sealed class YahooApiError
{
    public string? Code { get; init; }
    public string? Description { get; init; }
}

internal sealed class EastmoneySearchResponse
{
    [JsonPropertyName("QuotationCodeTable")]
    public EastmoneyQuotationCodeTable? QuotationCodeTable { get; init; }
}

internal sealed class EastmoneyQuotationCodeTable
{
    [JsonPropertyName("Data")]
    public EastmoneySearchResult[]? Data { get; init; }
}

internal sealed class EastmoneySearchResult
{
    [JsonPropertyName("Code")]
    public string? Code { get; init; }

    [JsonPropertyName("Name")]
    public string? Name { get; init; }

    [JsonPropertyName("Classify")]
    public string? Classify { get; init; }

    [JsonPropertyName("SecurityTypeName")]
    public string? SecurityTypeName { get; init; }

    [JsonPropertyName("JYS")]
    public string? Exchange { get; init; }
}

internal sealed class EastmoneyQuoteResponse
{
    [JsonPropertyName("data")]
    public EastmoneyQuoteData? Data { get; init; }
}

internal sealed class EastmoneyQuoteData
{
    [JsonPropertyName("f43")]
    public long? Price { get; init; }

    [JsonPropertyName("f44")]
    public long? DayHigh { get; init; }

    [JsonPropertyName("f45")]
    public long? DayLow { get; init; }

    [JsonPropertyName("f46")]
    public long? OpenPrice { get; init; }

    [JsonPropertyName("f47")]
    public long? Volume { get; init; }

    [JsonPropertyName("f60")]
    public long? PreviousClose { get; init; }
}

internal sealed record WatchlistDocument(List<SavedStock> Stocks);

internal sealed record SavedStock(string Symbol, string Name, string Exchange, string Market);

internal sealed record StockQuote(
    SavedStock Stock,
    decimal? Price,
    decimal? OpenPrice,
    decimal? PreviousClose,
    decimal? ChangePercent,
    decimal? DayHigh,
    decimal? DayLow,
    long? Volume,
    string? Currency,
    string? Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(YahooSearchResponse))]
[JsonSerializable(typeof(YahooChartResponse))]
[JsonSerializable(typeof(YahooSparkResponse))]
[JsonSerializable(typeof(EastmoneySearchResponse))]
[JsonSerializable(typeof(EastmoneyQuoteResponse))]
[JsonSerializable(typeof(WatchlistDocument))]
[JsonSerializable(typeof(AppSettings))]
internal partial class StockWatcherJsonContext : JsonSerializerContext;
