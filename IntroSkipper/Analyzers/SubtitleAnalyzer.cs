// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Detects recaps and previews from matching text subtitle cues.
/// </summary>
/// <remarks>
/// Subtitle detection is deliberately independent from credits detection. A recap starts at a
/// matching cue whose start lies inside the recap fingerprint window
/// (<see cref="QueuedEpisode.GetFingerprintRange"/>), and ends at the start of the stored intro
/// when its cue precedes the intro. When the intro comes first, or the episode has no intro,
/// only the next chapter start after the cue is a reliable end. Without one, the episode
/// settles with its standing rows, as for a match admission rejected. A preview
/// runs from its matching cue through the episode duration. When no subtitle cue matches, the
/// normal analyzer chain remains eligible. A pattern that is not a valid .NET regular expression
/// leaves every pending episode unresolved, so the mode stays open until the pattern is fixed.
/// </remarks>
internal sealed partial class SubtitleAnalyzer(
    ILogger<SubtitleAnalyzer> logger,
    IFFmpegService ffmpegService,
    IntroSkipperDatabase database,
    PluginConfiguration configuration) : IMediaFileAnalyzer
{
    private readonly ILogger<SubtitleAnalyzer> _logger = logger;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly IntroSkipperDatabase _database = database;
    private readonly PluginConfiguration _config = configuration;

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        if (_config.ActiveSubtitlePattern(mode) is not { } expression)
        {
            return analysisQueue;
        }

        Regex regex;
        try
        {
            regex = new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            // Unresolved keeps the standing rows and leaves the mode open until the pattern is
            // fixed. Without an outcome, the cleanup after the chain would delete a subtitle
            // row that had just blocked the other analyzers' writes, and the mode would settle
            // with no segment.
            LogInvalidPattern(_logger, mode, ex.Message);
            foreach (var episode in analysisQueue.Where(item => item.NeedsAnalysis(mode)))
            {
                episode.SetSubtitleOutcome(mode, SubtitleOutcome.Unresolved);
            }

            return analysisQueue;
        }

        foreach (var episode in analysisQueue.Where(item => item.NeedsAnalysis(mode)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scan = await _ffmpegService.ExtractSubtitleCuesAsync(episode, cancellationToken).ConfigureAwait(false);

            (Segment? Segment, bool DetectedWithoutEnd) recapSearch = mode == AnalysisMode.Recap
                ? await FindRecapAsync(episode, scan.Cues, regex, cancellationToken).ConfigureAwait(false)
                : (Segment: FindPreview(episode, scan.Cues, regex), DetectedWithoutEnd: false);
            var segment = recapSearch.Segment;

            if (segment is null)
            {
                if (!scan.Complete)
                {
                    episode.SetSubtitleOutcome(mode, SubtitleOutcome.Unresolved);
                    LogSubtitleScanIncomplete(_logger, episode.Name, mode);
                }
                else if (recapSearch.DetectedWithoutEnd)
                {
                    // Every retry would find the same cue without an end, so the episode
                    // settles with its standing rows, as for a rejected match.
                    episode.SetSubtitleOutcome(mode, SubtitleOutcome.Rejected);
                }
                else
                {
                    await _database.ClearSubtitleSegmentsAsync(episode.EpisodeId, mode, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            var written = await _database.ReplaceAutoSegmentsAsync(
                episode.EpisodeId,
                mode,
                [segment],
                SegmentSource.Subtitle,
                episode.AnalysisConfigHash,
                cancellationToken).ConfigureAwait(false);

            if (written > 0)
            {
                episode.SetAnalyzed(mode, EpisodeState.Analyzed);
                LogFoundSubtitleSegment(_logger, episode.Name, mode, segment.Start, segment.End);
            }

            // A match from an incomplete scan is kept but retried, since an unread source can
            // hold a better cue. A complete match that admission rejected settles the mode.
            if (!scan.Complete)
            {
                episode.SetSubtitleOutcome(mode, SubtitleOutcome.Unresolved);
            }
            else if (written == 0)
            {
                episode.SetSubtitleOutcome(mode, SubtitleOutcome.Rejected);
            }
        }

        return analysisQueue;
    }

    private async Task<(Segment? Segment, bool DetectedWithoutEnd)> FindRecapAsync(
        QueuedEpisode episode,
        IReadOnlyList<SubtitleCue> cues,
        Regex regex,
        CancellationToken cancellationToken)
    {
        // -1 without an intro, so every cue takes the later-chapter rule below.
        var introStart = (await _database.GetSegmentsAsync(episode.EpisodeId, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Where(segment => segment.Type == AnalysisMode.Introduction && segment.State == SegmentState.Active)
            .Select(segment => TickConversions.ToSeconds(segment.StartTicks))
            .Where(start => start >= 0 && start < episode.Duration)
            .OrderBy(start => start)
            .FirstOrDefault(-1);

        // Only cues that start in the window Chromaprint searches for recaps count, so a
        // matching line of dialogue later in the episode never becomes a recap.
        var (windowStart, windowEnd) = episode.GetFingerprintRange(AnalysisMode.Recap);
        foreach (var cue in cues.Where(cue => cue.Start >= windowStart && cue.Start < windowEnd))
        {
            if (!Matches(regex, cue.Text, episode.Name, AnalysisMode.Recap))
            {
                continue;
            }

            var recapEnd = cue.Start < introStart
                ? introStart
                : Plugin.Instance?.GetChapters(episode.EpisodeId)
                    .Select(chapter => TickConversions.ToSeconds(chapter.StartPositionTicks))
                    .Where(start => start > cue.Start && start < episode.Duration)
                    .OrderBy(start => start)
                    .FirstOrDefault() ?? 0;
            if (recapEnd <= cue.Start)
            {
                LogSubtitleRecapHasNoEnd(_logger, episode.Name, cue.Start);
                return (null, true);
            }

            var segment = new Segment(episode.EpisodeId, new TimeRange(cue.Start, recapEnd));
            if (segment.Valid
                && segment.Duration >= _config.MinimumRecapDuration
                && segment.Duration <= _config.MaximumRecapDuration)
            {
                return (segment, false);
            }
        }

        return (null, false);
    }

    private Segment? FindPreview(QueuedEpisode episode, IReadOnlyList<SubtitleCue> cues, Regex regex)
    {
        foreach (var cue in cues.OrderByDescending(cue => cue.Start))
        {
            if (!Matches(regex, cue.Text, episode.Name, AnalysisMode.Preview))
            {
                continue;
            }

            var segment = new Segment(episode.EpisodeId, new TimeRange(cue.Start, episode.Duration));
            if (segment.Valid
                && segment.Duration >= _config.MinimumPreviewDuration
                && segment.Duration <= _config.MaximumPreviewDuration)
            {
                return segment;
            }
        }

        return null;
    }

    private bool Matches(Regex regex, string text, string episode, AnalysisMode mode)
    {
        var plainText = SubtitleText(text);
        try
        {
            return regex.IsMatch(plainText);
        }
        catch (RegexMatchTimeoutException ex)
        {
            LogSubtitlePatternTimedOut(_logger, ex, episode, mode);
            return false;
        }
    }

    private static string SubtitleText(string text)
    {
        var withoutMarkup = MarkupRegex().Replace(text, " ");
        return string.Join(' ', withoutMarkup.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex("<[^>]*>|\\{[^}]*\\}", RegexOptions.CultureInvariant)]
    private static partial Regex MarkupRegex();

    [LoggerMessage(Level = LogLevel.Trace, Message = "{Episode}: subtitle {Mode} candidate [{Start:F2}, {End:F2}]")]
    private static partial void LogFoundSubtitleSegment(ILogger logger, string episode, AnalysisMode mode, double start, double end);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Episode}: subtitle recap detected at {Start:F2}s, but no later chapter marker provides a reliable end")]
    private static partial void LogSubtitleRecapHasNoEnd(ILogger logger, string episode, double start);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid subtitle {Mode} regular expression: {Message}")]
    private static partial void LogInvalidPattern(ILogger logger, AnalysisMode mode, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Episode}: subtitle {Mode} regular expression timed out; skipping cue")]
    private static partial void LogSubtitlePatternTimedOut(ILogger logger, Exception ex, string episode, AnalysisMode mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Episode}: some subtitle sources could not be read and no {Mode} cue matched; the mode will be retried")]
    private static partial void LogSubtitleScanIncomplete(ILogger logger, string episode, AnalysisMode mode);
}
