using System.Text.Json;
using StockWatcher.Models;

namespace StockWatcher.Services;

internal sealed class SettingsStore
{
    public SettingsStore(string? filePath = null)
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        FilePath = filePath ?? Path.Combine(directory, "stockWatcher", "settings.json");
    }

    public string FilePath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        await using var stream = File.OpenRead(FilePath);
        AppSettings settings;
        try
        {
            settings = await JsonSerializer.DeserializeAsync(stream,
                StockWatcherJsonContext.Default.AppSettings, cancellationToken) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"配置文件格式错误：{FilePath}", ex);
        }

        return settings.RefreshIntervalSeconds is < AppSettings.MinRefreshIntervalSeconds or > AppSettings.MaxRefreshIntervalSeconds
            ? settings with { RefreshIntervalSeconds = AppSettings.DefaultRefreshIntervalSeconds }
            : settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (settings.RefreshIntervalSeconds is < AppSettings.MinRefreshIntervalSeconds or > AppSettings.MaxRefreshIntervalSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "行情刷新间隔必须在 1～10 秒之间。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings,
                    StockWatcherJsonContext.Default.AppSettings, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
