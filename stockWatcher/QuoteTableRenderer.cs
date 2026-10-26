using System.Globalization;
using System.Text;
using StockWatcher.Models;

namespace StockWatcher;

internal static class QuoteTableRenderer
{
    public static void WriteShow(IReadOnlyList<StockQuote> quotes, DateTimeOffset updatedAt,
        bool maskBackground = false, bool revealChangeColors = false)
    {
        var interactive = !Console.IsOutputRedirected;
        var originalBackground = interactive ? Console.BackgroundColor : ConsoleColor.Black;
        var originalForeground = interactive ? Console.ForegroundColor : ConsoleColor.Gray;
        try
        {
            if (interactive)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
            }

            if (interactive && maskBackground)
            {
                Console.BackgroundColor = GetBackgroundColor(maskBackground, originalBackground);
            }

            WriteContent(quotes, updatedAt, revealChangeColors);
        }
        finally
        {
            if (interactive)
            {
                Console.ForegroundColor = originalForeground;
                Console.BackgroundColor = originalBackground;
            }
        }
    }

    internal static ConsoleColor GetBackgroundColor(bool masked, ConsoleColor originalBackground)
        => masked ? ConsoleColor.DarkGray : originalBackground;

    private static void WriteContent(IReadOnlyList<StockQuote> quotes, DateTimeOffset updatedAt, bool revealChangeColors)
    {
        if (!Console.IsOutputRedirected)
        {
            try
            {
                Console.Clear();
            }
            catch (IOException)
            {
            }
        }

        Console.WriteLine($"自选行情  更新于 {updatedAt:yyyy-MM-dd HH:mm:ss zzz}");
        Console.WriteLine($"{"code",-14} {"name",-32} {"price",14} {"limit",12} {"start",14}");
        Console.WriteLine(new string('-', 90));

        foreach (var quote in quotes.OrderByDescending(static quote => quote.ChangePercent))
        {
            Console.Write($"{quote.Stock.Symbol,-14} ");
            WriteColored(FitName(quote.Stock.Name, 32), ConsoleColor.DarkGray);
            Console.Write($" {FormatPrice(quote.Price),14} ");
            var change = quote.ChangePercent?.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
            WriteColored($"{(change is null ? "--" : change + "%"),12}", GetChangeColor(quote.ChangePercent, revealChangeColors));
            Console.WriteLine($" {FormatPrice(quote.OpenPrice),14}");
        }

        foreach (var quote in quotes.Where(static quote => quote.Error is not null))
        {
            Console.WriteLine($"{quote.Stock.Symbol}: {quote.Error}");
        }

    }

    internal static ConsoleColor GetChangeColor(decimal? changePercent, bool reveal = false)
        => reveal ? changePercent switch
        {
            > 0 => ConsoleColor.Red,
            < 0 => ConsoleColor.Green,
            _ => ConsoleColor.DarkGray
        } : ConsoleColor.DarkGray;

    private static string FormatPrice(decimal? price)
        => price?.ToString("N2", CultureInfo.InvariantCulture) ?? "--";

    private static void WriteColored(string text, ConsoleColor? color)
    {
        if (Console.IsOutputRedirected || color is null)
        {
            Console.Write(text);
            return;
        }

        var previousColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color.Value;
            Console.Write(text);
        }
        finally
        {
            Console.ForegroundColor = previousColor;
        }
    }

    private static string FitName(string value, int width)
    {
        var result = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeWidth = rune.Value is >= 0x1100 and <= 0x115F
                or >= 0x2E80 and <= 0xA4CF
                or >= 0xAC00 and <= 0xD7A3
                or >= 0xF900 and <= 0xFAFF
                or >= 0xFE10 and <= 0xFE6F
                or >= 0xFF01 and <= 0xFF60
                or >= 0xFFE0 and <= 0xFFE6
                or >= 0x20000 and <= 0x3FFFD ? 2 : 1;
            if (used + runeWidth > width - 1)
            {
                result.Append('…');
                used++;
                break;
            }

            result.Append(rune.ToString());
            used += runeWidth;
        }

        return result.Append(' ', width - used).ToString();
    }
}
