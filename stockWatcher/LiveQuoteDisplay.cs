using System.Runtime.InteropServices;
using StockWatcher.Models;

namespace StockWatcher;

internal sealed partial class LiveQuoteDisplay : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly bool _maskEnabled;
    private readonly bool _monitorAlt;
    private readonly Func<bool> _readAltPressed;
    private readonly Action<IReadOnlyList<StockQuote>, DateTimeOffset, bool, bool> _render;
    private readonly Action? _restore;
    private readonly CancellationTokenSource _stop;
    private readonly Task _watchTask;
    private IReadOnlyList<StockQuote>? _quotes;
    private DateTimeOffset _updatedAt;
    private bool _altPressed;

    internal LiveQuoteDisplay(bool maskEnabled, Func<bool> readAltPressed,
        Action<IReadOnlyList<StockQuote>, DateTimeOffset, bool, bool> render,
        CancellationToken cancellationToken, Action? restore = null, bool monitorAlt = false)
    {
        _maskEnabled = maskEnabled;
        _monitorAlt = maskEnabled || monitorAlt;
        _readAltPressed = readAltPressed;
        _render = render;
        _restore = restore;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _altPressed = _monitorAlt && readAltPressed();
        _watchTask = _monitorAlt ? WatchAltAsync() : Task.CompletedTask;
    }

    public static LiveQuoteDisplay Start(bool noMask, CancellationToken token)
    {
        var interactive = !Console.IsOutputRedirected && !Console.IsInputRedirected;
        var monitorAlt = interactive && OperatingSystem.IsWindows();
        var maskEnabled = monitorAlt && !noMask;
        var originalBackground = interactive ? Console.BackgroundColor : ConsoleColor.Black;
        return new LiveQuoteDisplay(maskEnabled, IsAltPressed,
            (quotes, updatedAt, masked, altPressed) => QuoteTableRenderer.WriteShow(quotes, updatedAt, masked, altPressed),
            token,
            maskEnabled ? () =>
            {
                Console.BackgroundColor = originalBackground;
                try
                {
                    Console.Clear();
                }
                catch (IOException)
                {
                }
            } : null, monitorAlt);
    }

    public void Update(IReadOnlyList<StockQuote> quotes, DateTimeOffset updatedAt)
    {
        lock (_sync)
        {
            _quotes = quotes;
            _updatedAt = updatedAt;
            _altPressed = _monitorAlt && _readAltPressed();
            Render();
        }
    }

    private void Render()
    {
        if (_quotes is not null)
        {
            _render(_quotes, _updatedAt, _maskEnabled && !_altPressed, _altPressed);
        }
    }

    private async Task WatchAltAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(30, _stop.Token);
                lock (_sync)
                {
                    var pressed = _readAltPressed();
                    if (pressed != _altPressed)
                    {
                        _altPressed = pressed;
                        Render();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            await _watchTask;
        }
        finally
        {
            _restore?.Invoke();
            _stop.Dispose();
        }
    }

    private static bool IsAltPressed()
        => OperatingSystem.IsWindows() && (GetAsyncKeyState(0x12) & 0x8000) != 0;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);
}
