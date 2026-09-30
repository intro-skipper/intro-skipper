// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using CreditsRunner;

try
{
    return args switch
    {
        ["run", .. var rest] => await Commands.RunAsync(Options.Parse(rest), Console.Out).ConfigureAwait(false),
        ["score", .. var rest] => Commands.Score(Options.Parse(rest), Console.Out),
        ["diff", .. var rest] => Commands.Diff(Options.Parse(rest), Console.Out),
        _ => Commands.Usage(Console.Error),
    };
}
catch (Exception e) when (e is ArgumentException or IOException or System.Text.Json.JsonException or InvalidDataException)
{
    await Console.Error.WriteLineAsync(e.Message).ConfigureAwait(false);
    return 2;
}
