using StockWatcher;
using StockWatcher.Services;

using var yahoo = new YahooFinanceClient();
var app = new StockWatcherApp(yahoo, new WatchlistStore(), new SettingsStore());
var command = app.BuildCommand();
return await command.Parse(args).InvokeAsync();
