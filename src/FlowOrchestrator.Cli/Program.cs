using System.Text;
using FlowOrchestrator.Cli;

// Row values in messages (keys, unicode text) print as written instead of '?' on a legacy Windows code page.
Console.OutputEncoding = Encoding.UTF8;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl+C cancels cleanly (the table in flight is rolled back); a second one kills the process.
    if (!cancellation.IsCancellationRequested)
    {
        e.Cancel = true;
        cancellation.Cancel();
    }
};

return await CliApp.RunAsync(args, Console.Out, Console.Error, Environment.GetEnvironmentVariable, cancellation.Token);
