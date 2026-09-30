// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using CreditsRunner;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

try
{
    return args switch
    {
        ["run", .. var rest] => await Commands.RunAsync(rest, Console.Out, stop.Token).ConfigureAwait(false),
        ["score", .. var rest] => Commands.Score(rest, Console.Out),
        ["diff", .. var rest] => Commands.Diff(rest, Console.Out),
        _ => Commands.Usage(Console.Error),
    };
}
catch (Exception e) when (e is ArgumentException or IOException or System.Text.Json.JsonException or InvalidDataException)
{
    await Console.Error.WriteLineAsync(e.Message).ConfigureAwait(false);
    return 2;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    await Console.Error.WriteLineAsync("cancelled").ConfigureAwait(false);
    return 130;
}
