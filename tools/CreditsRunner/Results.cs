// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreditsRunner;

/// <summary>
/// One credits range the analyzer proposed, in file time.
/// </summary>
/// <param name="Start">The start in seconds.</param>
/// <param name="End">The end in seconds.</param>
/// <param name="Source">The analyzer's source tag, such as <c>BlackFrame</c> or <c>Combined</c>.</param>
internal sealed record Candidate(double Start, double End, string Source)
{
    /// <summary>
    /// Gets the candidate as a span.
    /// </summary>
    [JsonIgnore]
    public Interval Span => new(Start, End);
}

/// <summary>
/// One call the analyzer made into the ffmpeg service, with the ffmpeg processes it started.
/// A call served from the detection cache starts none.
/// </summary>
/// <param name="Trigger">The service method, such as <c>ScanKeyframesAsync</c> or <c>DetectBlackIntervalsAsync</c>.</param>
/// <param name="Processes">The ffmpeg processes the call started.</param>
/// <param name="WallSeconds">The call's wall-clock time.</param>
/// <param name="CpuSeconds">The user and system CPU time of the processes it started; <see langword="null"/> off Linux.</param>
internal sealed record CallTiming(string Trigger, int Processes, double WallSeconds, double? CpuSeconds);

/// <summary>
/// One corpus file's result. With repeats, the times are medians over the timed passes.
/// </summary>
/// <param name="Id">The corpus id.</param>
/// <param name="WindowStart">The credits window's start in seconds.</param>
/// <param name="WindowEnd">The credits window's end in seconds.</param>
/// <param name="Combined">The candidates after the credits combiner: what the analyzer contributes to a stored row, and what the scorer judges.</param>
/// <param name="Raw">The analyzer's candidates before combining, for diagnosis.</param>
/// <param name="Failure">The exception that failed the episode, or <see langword="null"/>.</param>
/// <param name="Nondeterministic">Whether the passes disagreed on the candidates or on the calls made.</param>
/// <param name="WallSeconds">The analyzer's wall-clock time for the file.</param>
/// <param name="CpuSeconds">The CPU time of every ffmpeg process the analyzer started; <see langword="null"/> off Linux.</param>
/// <param name="PluginCpuSeconds">The runner process's own CPU time over the same span, which covers the analyzer's work.</param>
/// <param name="Calls">The analyzer's calls into the ffmpeg service, in order.</param>
internal sealed record EpisodeResult(
    string Id,
    double WindowStart,
    double WindowEnd,
    IReadOnlyList<Candidate> Combined,
    IReadOnlyList<Candidate> Raw,
    string? Failure,
    bool Nondeterministic,
    double WallSeconds,
    double? CpuSeconds,
    double PluginCpuSeconds,
    IReadOnlyList<CallTiming> Calls);

/// <summary>
/// One run of the analyzer over the corpus at one configuration.
/// </summary>
/// <param name="Plugin">The plugin assembly's informational version, which carries the commit.</param>
/// <param name="Ffmpeg">The first line of <c>ffmpeg -version</c>.</param>
/// <param name="Cpu">The CPU model.</param>
/// <param name="Os">The operating system.</param>
/// <param name="Repeats">Timed passes per file. Above one, an untimed pass warms the page cache first.</param>
/// <param name="Configuration">The plugin configuration the analyzer ran with.</param>
/// <param name="Episodes">One result per corpus file, in manifest order.</param>
internal sealed record RunResults(
    string Plugin,
    string Ffmpeg,
    string Cpu,
    string Os,
    int Repeats,
    JsonElement Configuration,
    IReadOnlyList<EpisodeResult> Episodes);
