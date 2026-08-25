// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Outcome of the most recent FFmpeg capability check, as shown in the support bundle.
/// </summary>
/// <param name="Status">Status token: <c>okay</c>, <c>unknown</c> before the first check has run, or the name of the failed requirement.</param>
/// <param name="Outputs">Raw output of every probe that ran, in check order.</param>
public sealed record FFmpegCheckResult(string Status, IReadOnlyList<FFmpegCheckOutput> Outputs)
{
    /// <summary>
    /// Gets the result reported before <see cref="IFFmpegService.CheckFFmpegVersionAsync"/> has run.
    /// </summary>
    public static FFmpegCheckResult NotRun { get; } = new("unknown", []);
}
