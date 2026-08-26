using System.Text;
using Spectre.Console;
using WindowsCleanup;
using WindowsCleanup.Localization;

Console.OutputEncoding = Encoding.UTF8;

if (!CliOptionsParser.TryParse(args, out var options, out var error))
{
	var errorLanguage = LanguageDetector.Detect();
	var errorText = new Localizer(errorLanguage);
	if (!string.IsNullOrWhiteSpace(error))
	{
		AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
	}

	CliOptionsParser.WriteUsage(errorText);
	return 2;
}

var text = new Localizer(LanguageDetector.Detect());
var app = new CleanupApp(text);
return await app.RunAsync(options);
