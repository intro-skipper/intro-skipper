// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Frozen;
using System.Text.RegularExpressions;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Chapter name analyzer.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ChapterAnalyzer"/> class.
/// </remarks>
/// <param name="logger">Logger.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="database">Segment database facade.</param>
/// <param name="configuration">Plugin configuration, or <see langword="null"/> to use the active plugin configuration.</param>
/// <param name="enableChapterDetection">Whether to detect chapter-based segments.</param>
/// <param name="enableRecapBlackFrameFallback">Whether recap detection may fall back to black-frame analysis.</param>
internal sealed partial class ChapterAnalyzer(
    ILogger<ChapterAnalyzer> logger,
    IFFmpegService ffmpegService,
    IntroSkipperDatabase database,
    PluginConfiguration? configuration = null,
    bool enableChapterDetection = true,
    bool enableRecapBlackFrameFallback = true) : IMediaFileAnalyzer
{
    private readonly ILogger<ChapterAnalyzer> _logger = logger;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly IntroSkipperDatabase _database = database;
    private readonly PluginConfiguration _config = configuration ?? Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private readonly bool _enableChapterDetection = enableChapterDetection;
    private readonly bool _enableRecapBlackFrameFallback = enableRecapBlackFrameFallback;
    private readonly Dictionary<AnalysisMode, string> _expressions = [];

    // Labels that could mean an intro, a recap or a preview; only the Commercial mode, which
    // skips them all, treats them as a match.
    private static readonly string[] _ambiguousSponsorBlockChapterLabels =
    [
        "intermission/intro animation",
        "preview/recap",
        "preview/recap/hook",
        "hook",
        "hook/greetings",
    ];

    private static readonly FrozenDictionary<AnalysisMode, FrozenSet<string>> _sponsorBlockChapterLabels =
        new Dictionary<AnalysisMode, FrozenSet<string>>
        {
            [AnalysisMode.Introduction] = Labels(["intro"]),
            [AnalysisMode.Credits] = Labels(["outro", "endcards/credits"]),
            [AnalysisMode.Preview] = Labels(["preview"]),
            [AnalysisMode.Recap] = Labels(["recap"]),
            [AnalysisMode.Commercial] = Labels(
            [
                "sponsor",
                "selfpromo",
                "self promotion",
                "unpaid/self promotion",
                "interaction",
                "interaction reminder",
                "interaction reminder (subscribe)",
                "intermission",
                "filler",
                "tangents/jokes",
                "music_offtopic",
                "music: non-music section",
                "non-music section",
                .. _ambiguousSponsorBlockChapterLabels,
            ]),
        }.ToFrozenDictionary();

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        var enableRecapBlackFrameFallback = _enableRecapBlackFrameFallback && mode == AnalysisMode.Recap && _config.DetectRecapUsingBlackFrames;
        var enableChapterDetection = _enableChapterDetection;
        var enableSponsorBlock = enableChapterDetection && _config.EnableSponsorBlockChapterDetection;
        var expression = enableChapterDetection ? GetExpression(mode) : string.Empty;

        if (string.IsNullOrWhiteSpace(expression) && !enableSponsorBlock && !enableRecapBlackFrameFallback)
        {
            return analysisQueue;
        }

        var timeAdjustmentHelper = new TimeAdjustmentHelper(_logger, _config, mode, _ffmpegService);

        var episodesWithoutIntros = analysisQueue.Where(e => e.NeedsAnalysis(mode)).ToList();

        foreach (var episode in episodesWithoutIntros)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<Segment> matches = enableChapterDetection ? FindChapterCandidates(episode, mode) : [];

            var source = SegmentSource.Chapter;
            if (matches.Count == 0 && enableRecapBlackFrameFallback)
            {
                Segment? fallback;
                try
                {
                    fallback = await DetectRecapUsingBlackFramesAsync(episode, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    episode.SetAnalyzed(mode, EpisodeState.AnalysisFailed);
                    LogErrorDetectingRecapBlackFrames(ex, episode.Name);
                    continue;
                }

                if (fallback is not null && fallback.Valid)
                {
                    matches = [fallback];
                    source = SegmentSource.BlackFrame;
                }
            }

            if (matches.Count == 0)
            {
                continue;
            }

            // The black-frame recap fallback's end is snapped to a keyframe, which can fail on
            // every pass for one file, such as one whose tags push the codec probe's stream
            // mapping past its capture. A chapter end makes no keyframe call, so only the
            // fallback and the write can fail here, and either fails only this episode.
            try
            {
                episode.SetAnalyzed(mode, await StoreMatchesAsync(episode, mode, matches, timeAdjustmentHelper, source, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                episode.SetAnalyzed(mode, EpisodeState.AnalysisFailed);
                LogErrorStoringMatches(ex, mode, episode.Name);
            }
        }

        return analysisQueue;
    }

    /// <summary>
    /// Adjusts chapter or black-frame recap matches and writes them as the episode's automatic
    /// segments for the mode. Shared by the first-wins chain and the credits pass so a result is
    /// stored the same way whichever path found it.
    /// </summary>
    /// <remarks>
    /// A chapter range sits on authored marks: it is not snapped to another chapter, silence or
    /// keyframe, but the configured offsets apply. A black-frame recap is adjusted like any
    /// detected range. A rejected non-empty candidate leaves the episode open for the next
    /// analyzer; when adjustments consume every match, the episode's stale automatic rows are
    /// still cleared.
    /// </remarks>
    /// <param name="episode">Episode.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="matches">The unadjusted chapter matches.</param>
    /// <param name="timeAdjustmentHelper">Adjustment helper initialized for <paramref name="mode"/>.</param>
    /// <param name="source">The analyzer source that produced the candidates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see cref="EpisodeState.Analyzed"/> when at least one match survived adjustment, otherwise <see cref="EpisodeState.NoSegments"/>.</returns>
    internal async Task<EpisodeState> StoreMatchesAsync(
        QueuedEpisode episode,
        AnalysisMode mode,
        IReadOnlyList<Segment> matches,
        TimeAdjustmentHelper timeAdjustmentHelper,
        SegmentSource source,
        CancellationToken cancellationToken)
    {
        var adjusted = new List<Segment>(matches.Count);
        foreach (var match in matches)
        {
            var adjustedSegment = await timeAdjustmentHelper.AdjustIntroTimesAsync(episode, match, source == SegmentSource.Chapter, cancellationToken).ConfigureAwait(false);
            if (adjustedSegment.Valid)
            {
                adjusted.Add(adjustedSegment);
            }
        }

        foreach (var segment in adjusted)
        {
            LogFoundChapter(episode.Name, mode, segment.Start, segment.End);
        }

        var written = await _database.ReplaceAutoSegmentsAsync(episode.EpisodeId, mode, adjusted, source, episode.AnalysisConfigHash, cancellationToken).ConfigureAwait(false);
        if (adjusted.Count == 0)
        {
            return EpisodeState.NoSegments;
        }

        return written > 0 ? EpisodeState.Analyzed : EpisodeState.NotAnalyzed;
    }

    /// <summary>
    /// Finds the episode's chapter matches for the mode without adjusting times or writing.
    /// The credits pass trusts credits matches that no story follows unless chapter enhancement
    /// is enabled (see <c>CreditsPass.AuthoritativeChapterMatches</c>).
    /// </summary>
    /// <param name="episode">Episode.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>The matching chapter ranges in file seconds; empty when chapter matching is off for the mode or nothing matched.</returns>
    internal IReadOnlyList<Segment> FindChapterCandidates(QueuedEpisode episode, AnalysisMode mode)
    {
        var expression = GetExpression(mode);
        return !string.IsNullOrWhiteSpace(expression) || _config.EnableSponsorBlockChapterDetection
            ? FindMatchingChapters(
                episode,
                episode.Chapters,
                expression,
                mode,
                _config.EnableSponsorBlockChapterDetection)
            : [];
    }

    /// <summary>
    /// Finds where the first chapter matching the mode starts at or after a time, by the mode's
    /// pattern and duration limits but without the single-match rules of
    /// <see cref="FindMatchingChapters"/>. The credits pass reads it for the preview chapter
    /// that ends the story after a credits chapter.
    /// </summary>
    /// <param name="episode">Episode.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="from">The earliest chapter start to consider, in file seconds.</param>
    /// <returns>The chapter start in file seconds, or <see langword="null"/> when no chapter from there on matches.</returns>
    internal double? FindFirstMatchStart(QueuedEpisode episode, AnalysisMode mode, double from)
    {
        var expression = GetExpression(mode);
        var (minDuration, maxDuration) = GetBounds(mode, episode);
        var chapters = episode.Chapters;
        for (var i = 0; i < chapters.Count; i++)
        {
            var name = chapters[i].Name;
            var start = TimeSpan.FromTicks(chapters[i].StartPositionTicks).TotalSeconds;
            var end = i + 1 < chapters.Count ? TimeSpan.FromTicks(chapters[i + 1].StartPositionTicks).TotalSeconds : episode.Duration;
            if (start >= from
                && !string.IsNullOrWhiteSpace(name)
                && end - start >= minDuration
                && end - start <= maxDuration
                && ChapterMatches(name, expression, mode, _config.EnableSponsorBlockChapterDetection))
            {
                return start;
            }
        }

        return null;
    }

    // The mode's chapter pattern, or empty when .NET cannot parse it. The dashboard checks the
    // pattern as a JavaScript regular expression, whose syntax differs from .NET's, so the
    // analyzer checks it again before use. It does so once per analyzer and mode and logs one
    // warning, instead of one per episode. SponsorBlock labels still match without a pattern.
    private string GetExpression(AnalysisMode mode)
    {
        if (_expressions.TryGetValue(mode, out var validated))
        {
            return validated;
        }

        var expression = mode switch
        {
            AnalysisMode.Introduction => _config.ChapterAnalyzerIntroductionPattern,
            AnalysisMode.Credits => _config.ChapterAnalyzerEndCreditsPattern,
            AnalysisMode.Recap => _config.ChapterAnalyzerRecapPattern,
            AnalysisMode.Preview => _config.ChapterAnalyzerPreviewPattern,
            AnalysisMode.Commercial => _config.ChapterAnalyzerCommercialPattern,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Unexpected analysis mode: {mode}")
        };
        if (!string.IsNullOrWhiteSpace(expression))
        {
            try
            {
                _ = Regex.IsMatch(string.Empty, expression, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                LogInvalidChapterPattern(mode, ex.Message);
                expression = string.Empty;
            }
        }

        _expressions[mode] = expression;
        return expression;
    }

    /// <summary>
    /// Searches a list of chapter names for all that match the provided regular expression,
    /// in mode-specific scan order (reversed for credits and previews).
    /// </summary>
    /// <param name="episode">Episode.</param>
    /// <param name="chapters">Media item chapters.</param>
    /// <param name="expression">A valid regular expression, or empty to match SponsorBlock labels only.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="enableSponsorBlockChapterDetection">Whether known SponsorBlock chapter labels should be matched in addition to the regular expression.</param>
    /// <returns>All matching skippable time ranges; empty when no chapter matched.</returns>
    internal IReadOnlyList<Segment> FindMatchingChapters(
        QueuedEpisode episode,
        IReadOnlyList<ChapterInfo> chapters,
        string expression,
        AnalysisMode mode,
        bool enableSponsorBlockChapterDetection = true)
    {
        var matches = new List<Segment>();
        var count = chapters.Count;
        if (count == 0)
        {
            return matches;
        }

        var reversed = mode == AnalysisMode.Credits || mode == AnalysisMode.Preview;
        var step = reversed ? -1 : 1;
        var (minDuration, maxDuration) = GetBounds(mode, episode);

        for (var i = reversed ? count - 1 : 0; i >= 0 && i < count; i += step)
        {
            var chapter = chapters[i];
            var next = chapters.ElementAtOrDefault(i + 1) ??
                new ChapterInfo { StartPositionTicks = TimeSpan.FromSeconds(episode.Duration).Ticks }; // Since the ending credits chapter may be the last chapter in the file, append a virtual chapter.

            if (string.IsNullOrWhiteSpace(chapter.Name))
            {
                continue;
            }

            var currentRange = new TimeRange(
                TimeSpan.FromTicks(chapter.StartPositionTicks).TotalSeconds,
                TimeSpan.FromTicks(next.StartPositionTicks).TotalSeconds);

            if (currentRange.Duration < minDuration || currentRange.Duration > maxDuration)
            {
                LogIgnoringInvalidDuration(episode.Path, chapter.Name, currentRange.Start, currentRange.End);
                continue;
            }

            var match = ChapterMatches(chapter.Name, expression, mode, enableSponsorBlockChapterDetection);

            if (!match)
            {
                LogIgnoringNoRegexMatch(episode.Path, chapter.Name, currentRange.Start, currentRange.End);
                continue;
            }

            // A matching neighbour makes the boundary ambiguous (overlapping keyword
            // expressions), so single-match modes drop the chapter rather than guess.
            // Multi-match modes keep it: consecutive matching chapters (Sponsor, then
            // Self-Promotion) are the normal shape of an ad break, and the skip would
            // discard every member of the run except the last.
            if (!AllowsMultipleMatches(mode))
            {
                // Check if the next (or previous for Credits) chapter also matches
                var adjacentChapter = reversed ? chapters.ElementAtOrDefault(i - 1) : next;
                if (adjacentChapter != null && !string.IsNullOrWhiteSpace(adjacentChapter.Name))
                {
                    var overlap = ChapterMatches(
                        adjacentChapter.Name,
                        expression,
                        mode,
                        enableSponsorBlockChapterDetection);

                    if (overlap)
                    {
                        LogIgnoringAdjacentMatch(episode.Path, chapter.Name, currentRange.Start, currentRange.End);
                        continue;
                    }
                }
            }

            LogChapterOk(episode.Path, chapter.Name, currentRange.Start, currentRange.End);
            matches.Add(new Segment(episode.EpisodeId, currentRange));
            if (!AllowsMultipleMatches(mode))
            {
                break;
            }
        }

        return matches;
    }

    /// <summary>
    /// Declares per-mode segment multiplicity for chapter analysis. Commercials
    /// legitimately occur several times per episode; for every other mode a second
    /// matching chapter is far more likely noise, so scanning stops at the first
    /// accepted match (in mode-specific scan order). Storage, sync and the API are
    /// plural for every mode — this is an analyzer-quality policy only.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <returns><c>true</c> when the mode may emit more than one chapter match.</returns>
    internal static bool AllowsMultipleMatches(AnalysisMode mode) => mode == AnalysisMode.Commercial;

    internal async Task<Segment?> DetectRecapUsingBlackFramesAsync(QueuedEpisode episode, CancellationToken cancellationToken)
    {
        var maxRecapBoundary = await RecapDetectionHelper.GetMaximumBoundaryAsync(
            _database,
            episode,
            _config,
            cancellationToken).ConfigureAwait(false);
        if (maxRecapBoundary <= 0)
        {
            return null;
        }

        var blackFrames = await RecapDetectionHelper.DetectAdaptiveBlackFramesAsync(
            _ffmpegService,
            episode,
            maxRecapBoundary,
            _config,
            cancellationToken).ConfigureAwait(false);

        return RecapDetectionHelper.BuildRecapFromBlackFrames(
            episode.EpisodeId,
            blackFrames,
            _config.MinimumRecapDetectionDuration,
            maxRecapBoundary);
    }

    private (double Min, double Max) GetBounds(AnalysisMode mode, QueuedEpisode episode)
    {
        if (_config.FullLengthChapters)
        {
            // Leave 1 second buffer at start and end
            return (1, episode.Duration - 1);
        }

        // Map analysis mode to duration bounds
        return mode switch
        {
            AnalysisMode.Introduction => (_config.MinimumIntroDuration, _config.MaximumIntroDuration),
            AnalysisMode.Credits => (_config.MinimumCreditsDuration,
                episode.Category == QueuedMediaCategory.Movie ? _config.MaximumMovieCreditsDuration : _config.MaximumCreditsDuration),
            AnalysisMode.Recap => (_config.MinimumRecapDuration, _config.MaximumRecapDuration),
            AnalysisMode.Preview => (_config.MinimumPreviewDuration, _config.MaximumPreviewDuration),
            AnalysisMode.Commercial => (_config.MinimumCommercialDuration, _config.MaximumCommercialDuration),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Unsupported analysis mode: {mode}")
        };
    }

    private bool ChapterMatches(
        string chapterName,
        string expression,
        AnalysisMode mode,
        bool enableSponsorBlockChapterDetection)
    {
        if (enableSponsorBlockChapterDetection
            && TryGetSponsorBlockChapterLabel(chapterName, out var sponsorBlockLabel)
            && _sponsorBlockChapterLabels.TryGetValue(mode, out var labels)
            && labels.Contains(sponsorBlockLabel))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        // Regex.IsMatch() is used here in order to allow the runtime to cache the compiled regex
        // between function invocations.
        try
        {
            // CultureInvariant: under a Turkish server culture, IgnoreCase does not fold I to i,
            // so "END CREDITS" would miss "Credits".
            return Regex.IsMatch(
                chapterName,
                expression,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
        catch (RegexMatchTimeoutException)
        {
            // A pattern that backtracks for a second on a chapter name is the user's
            // configuration, not a transient failure. The chapter counts as no match, as a
            // subtitle pattern does (SubtitleAnalyzer), instead of stopping the season's
            // remaining modes through the pass's TimeoutException handler, which this
            // exception derives from.
            LogChapterPatternTimedOut(mode, chapterName);
            return false;
        }
    }

    private static FrozenSet<string> Labels(string[] labels) => labels.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static bool TryGetSponsorBlockChapterLabel(string chapterName, out string label)
    {
        const string Prefix = "[SponsorBlock]:";

        if (!chapterName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            label = string.Empty;
            return false;
        }

        label = chapterName[Prefix.Length..].Trim();
        return true;
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "{Path}: Chapter \"{Name}\" ({Start} - {End}): ignoring (invalid duration)")]
    private partial void LogIgnoringInvalidDuration(string path, string name, double start, double end);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{Path}: Chapter \"{Name}\" ({Start} - {End}): ignoring (does not match regular expression)")]
    private partial void LogIgnoringNoRegexMatch(string path, string name, double start, double end);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{Path}: Chapter \"{Name}\" ({Start} - {End}): ignoring (adjacent chapter also matches)")]
    private partial void LogIgnoringAdjacentMatch(string path, string name, double start, double end);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{Path}: Chapter \"{Name}\" ({Start} - {End}): okay")]
    private partial void LogChapterOk(string path, string name, double start, double end);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Found {Mode} chapter for {Episode} at {Start:F2}s to {End:F2}s")]
    private partial void LogFoundChapter(string episode, AnalysisMode mode, double start, double end);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error detecting recap black frames for {Episode}")]
    private partial void LogErrorDetectingRecapBlackFrames(Exception ex, string episode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error adjusting or storing the {Mode} chapter result for {Episode}")]
    private partial void LogErrorStoringMatches(Exception ex, AnalysisMode mode, string episode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid chapter pattern for {Mode}; skipping regex chapter matches: {Message}")]
    private partial void LogInvalidChapterPattern(AnalysisMode mode, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Mode} chapter pattern timed out on chapter \"{Name}\"; the chapter counts as no match. Simplify the pattern in the plugin settings.")]
    private partial void LogChapterPatternTimedOut(AnalysisMode mode, string name);
}
