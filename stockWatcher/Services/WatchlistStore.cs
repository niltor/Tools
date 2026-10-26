using System.Text.Json;
using StockWatcher.Models;

namespace StockWatcher.Services;

internal sealed class WatchlistStore
{
    private readonly string _filePath;

    public WatchlistStore()
    {
        var dataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        _filePath = Path.Combine(dataDirectory, "stockWatcher", "watchlist.json");
    }

    public async Task<List<SavedStock>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(_filePath);
        var document = await JsonSerializer.DeserializeAsync(
            stream,
            StockWatcherJsonContext.Default.WatchlistDocument,
            cancellationToken);

        return document?.Stocks ?? [];
    }

    public async Task SaveAsync(IReadOnlyCollection<SavedStock> stocks, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);

        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                new WatchlistDocument(stocks.ToList()),
                StockWatcherJsonContext.Default.WatchlistDocument,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    public string FilePath => _filePath;
}
