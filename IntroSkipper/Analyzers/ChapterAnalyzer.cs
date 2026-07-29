// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Immutable;
using System.Globalization;
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
public partial class ChapterAnalyzer(ILogger<ChapterAnalyzer> logger, IFFmpegService ffmpegService, IIntroSkipperDatabase database) : IMediaFileAnalyzer
{
    private readonly ILogger<ChapterAnalyzer> _logger = logger;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly IIntroSkipperDatabase _database = database;
    private readonly PluginConfiguration _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private static readonly ImmutableHashSet<string> _ambiguousSponsorBlockChapterLabels =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "intermission/intro animation",
            "preview/recap",
            "preview/recap/hook",
            "hook",
            "hook/greetings");

    private static readonly ImmutableDictionary<AnalysisMode, ImmutableHashSet<string>> _sponsorBlockChapterLabels =
        new Dictionary<AnalysisMode, ImmutableHashSet<string>>
        {
            [AnalysisMode.Introduction] = ImmutableHashSet.Create(
                StringComparer.OrdinalIgnoreCase,
                "intro"),
            [AnalysisMode.Credits] = ImmutableHashSet.Create(
                StringComparer.OrdinalIgnoreCase,
                "outro",
                "endcards/credits"),
            [AnalysisMode.Preview] = ImmutableHashSet.Create(
                StringComparer.OrdinalIgnoreCase,
                "preview"),
            [AnalysisMode.Recap] = ImmutableHashSet.Create(
                StringComparer.OrdinalIgnoreCase,
                "recap"),
            [AnalysisMode.Commercial] = ImmutableHashSet.Create(
                StringComparer.OrdinalIgnoreCase,
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
                "non-music section").Union(_ambiguousSponsorBlockChapterLabels)
        }.ToImmutableDictionary();

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        var enableRecapBlackFrameFallback = mode == AnalysisMode.Recap && _config.DetectRecapUsingBlackFrames;
        var expression = mode switch
        {
            AnalysisMode.Introduction => _config.ChapterAnalyzerIntroductionPattern,
            AnalysisMode.Credits => _config.ChapterAnalyzerEndCreditsPattern,
            AnalysisMode.Recap => _config.ChapterAnalyzerRecapPattern,
            AnalysisMode.Preview => _config.ChapterAnalyzerPreviewPattern,
            AnalysisMode.Commercial => _config.ChapterAnalyzerCommercialPattern,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Unexpected analysis mode: {mode}")
        };

        if (string.IsNullOrWhiteSpace(expression) && !_config.EnableSponsorBlockChapterDetection && !enableRecapBlackFrameFallback)
        {
            return analysisQueue;
        }

        var timeAdjustmentHelper = new TimeAdjustmentHelper(_logger, _config, mode, _ffmpegService);

        var episodesWithoutIntros = analysisQueue.Where(e => e.NeedsAnalysis(mode)).ToList();

        foreach (var episode in episodesWithoutIntros)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<Segment> matches = !string.IsNullOrWhiteSpace(expression) || _config.EnableSponsorBlockChapterDetection
                ? FindMatchingChapters(
                    episode,
                    Plugin.Instance!.GetChapters(episode.EpisodeId),
                    expression,
                    mode,
                    _config.EnableSponsorBlockChapterDetection)
                : [];

            if (matches.Count == 0 && enableRecapBlackFrameFallback)
            {
                var fallback = await DetectRecapUsingBlackFramesAsync(episode, cancellationToken).ConfigureAwait(false);
                if (fallback is not null && fallback.Valid)
                {
                    matches = [fallback];
                }
            }

            if (matches.Count == 0)
            {
                continue;
            }

            // The helper is initialized with the current mode, so recap fallback segments
            // still receive the same mode-specific boundary adjustments as chapter matches.
            var adjusted = new List<Segment>(matches.Count);
            foreach (var match in matches)
            {
                var adjustedSegment = await timeAdjustmentHelper.AdjustIntroTimesAsync(episode, match, false, cancellationToken).ConfigureAwait(false);
                if (adjustedSegment.Valid)
                {
                    adjusted.Add(adjustedSegment);
                }
            }

            if (adjusted.Count == 0)
            {
                continue;
            }

            episode.SetAnalyzed(mode, EpisodeState.Analyzed);
            await _database.ReplaceAutoSegmentsAsync(episode.EpisodeId, mode, adjusted, SegmentSource.Chapter, episode.AnalysisConfigHash, cancellationToken).ConfigureAwait(false);
        }

        return analysisQueue;
    }

    /// <summary>
    /// Searches a list of chapter names for all that match the provided regular expression,
    /// in mode-specific scan order (reversed for credits and previews).
    /// Only public to allow for unit testing.
    /// </summary>
    /// <param name="episode">Episode.</param>
    /// <param name="chapters">Media item chapters.</param>
    /// <param name="expression">Regular expression pattern.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="enableSponsorBlockChapterDetection">Whether known SponsorBlock chapter labels should be matched in addition to the regular expression.</param>
    /// <returns>All matching skippable time ranges; empty when no chapter matched.</returns>
    public IReadOnlyList<Segment> FindMatchingChapters(
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
        var (minDuration, maxDuration) = GetBounds(mode, episode);

        // Check all chapters
        for (int i = reversed ? count - 1 : 0; reversed ? i >= 0 : i < count; i += reversed ? -1 : 1)
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

            var baseMessage = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: Chapter \"{1}\" ({2} - {3})",
                    episode.Path,
                    chapter.Name,
                    currentRange.Start,
                    currentRange.End);

            if (currentRange.Duration < minDuration || currentRange.Duration > maxDuration)
            {
                LogIgnoringInvalidDuration(baseMessage);
                continue;
            }

            var match = ChapterMatches(chapter.Name, expression, mode, enableSponsorBlockChapterDetection);

            if (!match)
            {
                LogIgnoringNoRegexMatch(baseMessage);
                continue;
            }

            // Check if the next (or previous for Credits) chapter also matches
            var adjacentChapter = reversed ? chapters.ElementAtOrDefault(i - 1) : next;
            if (adjacentChapter != null && !string.IsNullOrWhiteSpace(adjacentChapter.Name))
            {
                // Check for possibility of overlapping keywords
                var overlap = ChapterMatches(
                    adjacentChapter.Name,
                    expression,
                    mode,
                    enableSponsorBlockChapterDetection);

                if (overlap)
                {
                    LogIgnoringAdjacentMatch(baseMessage);
                    continue;
                }
            }

            LogChapterOk(baseMessage);
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

        var blackFrames = await _ffmpegService.DetectBlackFramesAsync(
            episode,
            new TimeRange(0, maxRecapBoundary),
            _config.BlackFrameMinimumPercentage,
            _config.BlackFrameThreshold,
            AnalysisMode.Recap,
            cancellationToken).ConfigureAwait(false);

        return BuildRecapFromBlackFrames(
            episode.EpisodeId,
            blackFrames,
            _config.MinimumRecapDetectionDuration,
            maxRecapBoundary);
    }

    internal static Segment? BuildRecapFromBlackFrames(
        Guid episodeId,
        IReadOnlyList<BlackFrame> blackFrames,
        int minimumRecapDuration,
        double maximumRecapBoundary)
    {
        BlackFrame? selectedBlackFrame = null;
        foreach (var blackFrame in blackFrames)
        {
            if (blackFrame.Time < minimumRecapDuration || blackFrame.Time > maximumRecapBoundary)
            {
                continue;
            }

            if (selectedBlackFrame is null || blackFrame.Time > selectedBlackFrame.Time)
            {
                selectedBlackFrame = blackFrame;
            }
        }

        if (selectedBlackFrame is null)
        {
            return null;
        }

        return new Segment(episodeId, new TimeRange(0, selectedBlackFrame.Time));
    }

    private (double Min, double Max) GetBounds(AnalysisMode mode, QueuedEpisode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);

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

    private static bool ChapterMatches(
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
        return Regex.IsMatch(
            chapterName,
            expression,
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
    }

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

    [LoggerMessage(Level = LogLevel.Trace, Message = "{Base}: ignoring (invalid duration)")]
    private partial void LogIgnoringInvalidDuration(string @base);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{Base}: ignoring (does not match regular expression)")]
    private partial void LogIgnoringNoRegexMatch(string @base);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{Base}: ignoring (adjacent chapter also matches)")]
    private partial void LogIgnoringAdjacentMatch(string @base);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{Base}: okay")]
    private partial void LogChapterOk(string @base);
}
