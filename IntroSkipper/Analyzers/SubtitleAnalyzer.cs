// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
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
/// Subtitle detection is deliberately independent from credits detection. A recap starts at the
/// first matching cue whose start lies inside the recap fingerprint window
/// (<see cref="QueuedEpisode.GetFingerprintRange"/>), and ends at a chapter start or, without
/// one, at the start of a black run, within the recap duration limits and before an intro that
/// follows the cue. Without such an end, the episode settles with its standing rows, as for a
/// match admission rejected. A preview runs from its matching cue through the episode duration.
/// When no subtitle cue matches, the normal analyzer chain remains eligible. A pattern that is
/// not a valid .NET regular expression leaves every pending episode unresolved, so the mode
/// stays open until the pattern is fixed.
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
        // A season shares one override, so every enabled episode reads the same pattern.
        var enabledEpisodes = analysisQueue.Where(item => item.NeedsAnalysis(mode) && _config.ActiveSubtitlePattern(item, mode) is not null).ToArray();
        if (enabledEpisodes.Length == 0 || _config.ActiveSubtitlePattern(enabledEpisodes[0], mode) is not { } expression)
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
            foreach (var episode in enabledEpisodes)
            {
                episode.SetSubtitleOutcome(mode, SubtitleOutcome.Unresolved);
            }

            return analysisQueue;
        }

        foreach (var episode in enabledEpisodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = CueWindow(episode, mode);
            var scan = await _ffmpegService.ExtractSubtitleCuesAsync(episode, window, cancellationToken).ConfigureAwait(false);
            var cues = scan.Cues.Where(cue => cue.Start >= window.Start && cue.Start < window.End).ToArray();

            (Segment? Segment, bool DetectedWithoutEnd) recapSearch = mode == AnalysisMode.Recap
                ? await FindRecapAsync(episode, cues, regex, cancellationToken).ConfigureAwait(false)
                : (Segment: FindPreview(episode, cues, regex), DetectedWithoutEnd: false);
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
            // hold a better cue. A complete match that admission or a current chapter row
            // refused settles the mode.
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

    // The media seconds where a mode's matching cue must start. A recap starts inside the window
    // Chromaprint searches for recaps, so a matching line of dialogue later in the episode never
    // becomes one. A preview runs to the end and must fit MaximumPreviewDuration. Extraction reads
    // only this window of the embedded streams and still returns every cue a full read finds in
    // it, so the window needs no token in the analysis hash.
    private TimeRange CueWindow(QueuedEpisode episode, AnalysisMode mode)
    {
        if (mode == AnalysisMode.Recap)
        {
            var (start, end) = episode.GetFingerprintRange(AnalysisMode.Recap);
            return new TimeRange(start, end);
        }

        return new TimeRange(episode.Duration - _config.MaximumPreviewDuration, episode.Duration);
    }

    // The first matching cue starts the recap, and one without an end settles the episode: a
    // later match is another track's copy of the title card or a line of dialogue.
    private async Task<(Segment? Segment, bool DetectedWithoutEnd)> FindRecapAsync(
        QueuedEpisode episode,
        IReadOnlyList<SubtitleCue> cues,
        Regex regex,
        CancellationToken cancellationToken)
    {
        var cue = cues.FirstOrDefault(cue => Matches(regex, cue.Text, episode.Name, AnalysisMode.Recap));
        if (cue is null)
        {
            return (null, false);
        }

        if (await FindRecapEndAsync(episode, cue.Start, cancellationToken).ConfigureAwait(false) is not { } end)
        {
            LogSubtitleRecapHasNoEnd(_logger, episode.Name, cue.Start);
            return (null, true);
        }

        return (new Segment(episode.EpisodeId, new TimeRange(cue.Start, end)), false);
    }

    // A recap that starts at a cue ends between MinimumRecapDuration and MaximumRecapDuration
    // after it, and no later than an intro that follows the cue. The intro start itself is no
    // end: a cold open often plays between the recap and the intro. Inside that range the end
    // is the first chapter start, else the start of the first black run of at least
    // RecapDetectionHelper.MinimumEndingBlackRun seconds. Black runs come from the scan
    // Chromaprint and black-frame recaps make, over the same range, so the episode is scanned
    // once; a recap ending after that range can end only at a chapter. Null when nothing
    // qualifies. Changing this rule needs a new subtitle recap end token in ConfigHasher.
    private async Task<double?> FindRecapEndAsync(QueuedEpisode episode, double cueStart, CancellationToken cancellationToken)
    {
        var introAfterCue = (await _database.GetSegmentsAsync(episode.EpisodeId, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Where(segment => segment.Type == AnalysisMode.Introduction && segment.State == SegmentState.Active)
            .Select(segment => TickConversions.ToSeconds(segment.StartTicks))
            .Where(start => start > cueStart)
            .DefaultIfEmpty(double.MaxValue)
            .Min();
        var earliest = cueStart + _config.MinimumRecapDuration;
        var latest = Math.Min(Math.Min(cueStart + _config.MaximumRecapDuration, introAfterCue), episode.Duration);

        var chapterEnd = episode.Chapters
            .Select(chapter => TickConversions.ToSeconds(chapter.StartPositionTicks))
            .Where(start => start >= earliest && start <= latest)
            .Select(start => (double?)start)
            .Min();
        if (chapterEnd is not null)
        {
            return chapterEnd;
        }

        var scanEnd = await RecapDetectionHelper.GetMaximumBoundaryAsync(_database, episode, _config, cancellationToken).ConfigureAwait(false);
        var latestRun = Math.Min(latest, scanEnd);
        if (latestRun < earliest)
        {
            return null;
        }

        var blackFrames = await RecapDetectionHelper.DetectAdaptiveBlackFramesAsync(_ffmpegService, episode, scanEnd, _config, cancellationToken).ConfigureAwait(false);
        return RecapDetectionHelper.FirstBlackRunStart(blackFrames, RecapDetectionHelper.MinimumEndingBlackRun, earliest, latestRun);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "{Episode}: subtitle recap detected at {Start:F2}s, but no chapter or black run provides a reliable end")]
    private static partial void LogSubtitleRecapHasNoEnd(ILogger logger, string episode, double start);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid subtitle {Mode} regular expression: {Message}")]
    private static partial void LogInvalidPattern(ILogger logger, AnalysisMode mode, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Episode}: subtitle {Mode} regular expression timed out; skipping cue")]
    private static partial void LogSubtitlePatternTimedOut(ILogger logger, Exception ex, string episode, AnalysisMode mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Episode}: some subtitle sources could not be read and no {Mode} cue matched; the mode will be retried")]
    private static partial void LogSubtitleScanIncomplete(ILogger logger, string episode, AnalysisMode mode);
}
