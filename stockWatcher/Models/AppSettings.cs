namespace StockWatcher.Models;

internal sealed record AppSettings
{
    public const int DefaultRefreshIntervalSeconds = 3;
    public const int MinRefreshIntervalSeconds = 1;
    public const int MaxRefreshIntervalSeconds = 10;

    public int RefreshIntervalSeconds { get; init; } = DefaultRefreshIntervalSeconds;
    public bool MaskBackground { get; init; } = true;
}
