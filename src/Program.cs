using OwlCore.Console.TalkBox.Commands.Play;
using OwlCore.Console.TalkBox.Commands.Say;
using OwlCore.Console.TalkBox.Commands.Whisper;
using OwlCore.Diagnostics;
using System.CommandLine;

var startTime = DateTime.Now;

// Cancellation
var cancellationTokenSource = new CancellationTokenSource();
var cancellationToken = cancellationTokenSource.Token;

Console.CancelKeyPress += (sender, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationTokenSource.Cancel();
};

// Logging
var noLogArg = args.FirstOrDefault(x => x.Contains("-nolog"));
var noLog = noLogArg is not null;
if (noLog)
{
    args = [.. args.Where(x => x != noLogArg)];
}
else 
{
    Logger.MessageReceived += Logger_MessageReceived;
    void Logger_MessageReceived(object? sender, LoggerMessageEventArgs e)
    {
        if (e.Message.Contains("skipping") && e.Message.Contains("Event stream entry"))
            return;

        if (e.Level == LogLevel.Trace)
        {
            return;
        }

        // Set console color based on log level
        var originalColor = Console.ForegroundColor;
        switch (e.Level)
        {
            case LogLevel.Warning:
                Console.ForegroundColor = ConsoleColor.Yellow;
                break;
            case LogLevel.Error:
            case LogLevel.Critical:
                Console.ForegroundColor = ConsoleColor.Red;
                break;
        }

        try
        {
            Console.WriteLine($"+{Math.Round((DateTime.Now - startTime).TotalMilliseconds)}ms {Path.GetFileNameWithoutExtension(e.CallerFilePath)} {e.CallerMemberName}  [{e.Level}] {e.Exception} {e.Message}");
        }
        finally
        {
            // Always restore the original color
            Console.ForegroundColor = originalColor;
        }
    }
}

/* Custom startup args
Move closing comment up to enable (fully or partially) 
var customStartupArgs = "";
args = customStartupArgs.Split(' ').ToArray();
*/

AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) => Logger.LogError(e.ExceptionObject?.ToString() ?? "Error message not found", e.ExceptionObject as Exception);
AppDomain.CurrentDomain.FirstChanceException += (object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e) => Logger.LogError(e.Exception?.ToString() ?? "Error message not found", e.Exception);
TaskScheduler.UnobservedTaskException += (object? sender, UnobservedTaskExceptionEventArgs e) => Logger.LogError(e.Exception?.ToString() ?? "Error message not found", e.Exception);

// Entry
var rootCommand = new RootCommand("A command line environment for prototyping and utilizing OwlCore libraries related to speech and sound.")
{
    new WhisperListenCommand(cancellationToken),
    new SayCommand(cancellationToken),
    new PlayCommand(cancellationToken),
};

await rootCommand.Parse(args).InvokeAsync();