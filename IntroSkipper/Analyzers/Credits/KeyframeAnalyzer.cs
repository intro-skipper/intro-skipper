// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Detects credits from the keyframe scan: a black roll from black-frame evidence and card credits
/// from keyframe visuals, each as its own candidate for the credits pass.
/// </summary>
/// <remarks>
/// One decode reports both. Black-frame evidence is frame-accurate for credits on black and goes
/// through density gating, blackdetect interval recovery for sparse candidates and optional boundary
/// refinement. Three rules use the visuals on black keyframes: a keyframe whose background is
/// saturated is a dark tinted scene and does not count as black, a black scene starts after leading
/// keyframes that show dim content rather than black, a dim last shot before the cut to the roll, and
/// a black scene lettered on no more than half its pages is a gap between acts, not credits. With
/// boundary refinement on, the frames between the keyframes move the start after a lead-in back from
/// the first keyframe at the level over the frames that look like it (see <see cref="LeadInProbe"/>);
/// the probe decodes for the picked scene only and its start is cached under the two keyframes.
/// The card run is found against the black scenes those rules accepted: once they accept any scene,
/// a black keyframe is a card only inside one (see <see cref="CardRunFinder"/>).
/// </remarks>
/// <param name="logger">Logger for the analyzer.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="cacheService">Detection cache, for the lead-in probe's start.</param>
/// <param name="configuration">Plugin configuration, or <see langword="null"/> to use the active plugin configuration.</param>
internal sealed partial class KeyframeAnalyzer(
    ILogger<KeyframeAnalyzer> logger,
    IFFmpegService ffmpegService,
    DetectionCacheService cacheService,
    PluginConfiguration? configuration = null)
{
    // Black sits at 16 on the limited-range scale the scan is pinned to; a scene whose black keyframes
    // typically sit higher has lifted blacks and sets its own level. A couple of levels over it is
    // encoder noise, more is a dark grey scene.
    private const double LimitedRangeBlack = 16;
    private const double BlackLevelTolerance = 2;

    private readonly PluginConfiguration _config = configuration ?? Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private readonly ILogger<KeyframeAnalyzer> _logger = logger;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly DetectionCacheService _cacheService = cacheService;

    /// <summary>
    /// Detects one episode's credits with the configured thresholds, without adjusting times or
    /// writing. The credits pass combines the result with the other analyzers' candidates.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="cancellationToken">Token used to cancel FFmpeg probing.</param>
    /// <returns>Zero, one or two candidates: the black-frame candidate under <see cref="SegmentSource.BlackFrame"/> and the card run under <see cref="SegmentSource.KeyframeVisuals"/>. Failures of the keyframe scans and of boundary refinement propagate to the caller, which marks the episode failed; a failed interval probe or lead-in decode is logged and falls back.</returns>
    internal Task<IReadOnlyList<AttributedSegment>> DetectCreditsAsync(QueuedEpisode episode, CancellationToken cancellationToken)
        => DetectCreditsAsync(episode, _config.BlackFrameMinimumPercentage, _config.BlackFrameThreshold, _config.MinimumCreditsDuration, _config.DetectNonBlackCredits, cancellationToken);

    /// <summary>
    /// Detects the credits from FFmpeg keyframe evidence with explicit thresholds.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="minimumPercentage">Minimum percentage of the frame that must be black.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="minimumDuration">Minimum duration of the credits.</param>
    /// <param name="detectCardCredits">Whether to look for a card run in the keyframe visuals as well.</param>
    /// <param name="cancellationToken">Token used to cancel FFmpeg probing.</param>
    /// <returns>Zero, one or two candidates: the black-frame candidate under <see cref="SegmentSource.BlackFrame"/> and the card run under <see cref="SegmentSource.KeyframeVisuals"/>.</returns>
    internal async Task<IReadOnlyList<AttributedSegment>> DetectCreditsAsync(QueuedEpisode episode, int minimumPercentage, int threshold, int minimumDuration, bool detectCardCredits, CancellationToken cancellationToken = default)
    {
        var blackFrames = (await _ffmpegService.DetectBlackFramesAsync(episode, threshold, cancellationToken).ConfigureAwait(false)).ToList();

        // The keyframe scan that produced the black-frame row wrote the visuals row too, so this is
        // normally a cache read. Empty on an ffmpeg without signalstats, which leaves the gates on
        // black keyframes inert.
        var visuals = await _ffmpegService.DetectKeyframeVisualsAsync(episode, cancellationToken).ConfigureAwait(false);

        // Tinted keyframes are content to the black-frame rules, so they leave before the thresholds
        // are normalized against the scan they will be applied to.
        var sceneFrames = WithoutTintedKeyframes(blackFrames, visuals);
        var (blackMinimum, sceneChange) = sceneFrames.Count > 0
            ? BlackFrameThresholdHelper.NormalizeThreshold(sceneFrames, minimumPercentage)
            : (minimumPercentage, minimumPercentage);
        var (credits, scenes, rejected) = sceneFrames.Count > 0
            ? await DetectBlackFrameCreditsAsync(episode, sceneFrames, visuals, blackMinimum, sceneChange, threshold, minimumDuration, cancellationToken).ConfigureAwait(false)
            : (null, [], []);

        var candidates = new List<AttributedSegment>(2);
        if (credits is not null)
        {
            candidates.Add(new AttributedSegment(credits, SegmentSource.BlackFrame));
        }

        if (detectCardCredits)
        {
            var range = CardRunFinder.FindCreditRange(visuals, blackFrames, blackMinimum, minimumDuration, scenes, rejected);
            if (range is not null)
            {
                candidates.Add(new AttributedSegment(
                    new Segment(episode.EpisodeId, new TimeRange(range.Start + episode.CreditsFingerprintStart, range.End + episode.CreditsFingerprintStart)),
                    SegmentSource.KeyframeVisuals));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Detects credits from black-frame keyframe evidence, with optional blackdetect interval recovery and boundary refinement.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="blackFrames">The keyframe black-frame scan results.</param>
    /// <param name="visuals">The keyframe visuals of the same scan, or empty when the ffmpeg build has no signalstats filter.</param>
    /// <param name="minimum">The black percentage at or above which a keyframe is black, normalized against the scan.</param>
    /// <param name="sceneChange">The black percentage that marks the transition into credits, normalized against the scan.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="minimumDuration">Minimum duration of the credits.</param>
    /// <param name="cancellationToken">Token used to cancel FFmpeg probing.</param>
    /// <returns>The credits candidate in file time and every black scene the rules accepted, relative to the credits fingerprint start, the picked one with its refined start, plus the scenes the lettering gate rejected as gaps; <see langword="null"/> and an empty accepted list when no accepted scene met the minimum duration.</returns>
    private async Task<(Segment? Credits, List<TimeRange> Scenes, List<TimeRange> Rejected)> DetectBlackFrameCreditsAsync(QueuedEpisode episode, List<BlackFrame> blackFrames, IReadOnlyList<KeyframeVisual> visuals, int minimum, int sceneChange, int threshold, int minimumDuration, CancellationToken cancellationToken)
    {
        var scenes = CreditSceneBuilder.DetectCreditScenes(blackFrames, minimum, sceneChange, minimumDuration, _config.RefineCreditsBoundary);
        var blackIntervals = Array.Empty<BlackInterval>();

        if (scenes.Count == 0)
        {
            var candidates = CreditSceneBuilder.FindRawScenes(blackFrames, minimum);
            if (candidates.Count == 0)
            {
                return (null, [], []);
            }

            blackIntervals = await DetectBlackIntervalsForCandidatesOrEmptyAsync(episode, candidates, threshold, minimum, minimumDuration, cancellationToken).ConfigureAwait(false);
            scenes = CreditSceneBuilder.DetectIntervalSupportedCreditScenes(blackFrames, blackIntervals, minimum, minimumDuration);
            if (scenes.Count == 0)
            {
                return (null, [], []);
            }
        }
        else if (scenes.Any(scene => CreditSceneMetricsCalculator.Calculate(blackFrames, scene, minimum).IsSparse(scene, minimumDuration)))
        {
            // Probe all candidates for ranking. When any are confirmed, keep dense scenes unchanged
            // and add interval-supported scenes that do not overlap them by frame range.
            // If none are confirmed, keep the original candidate set.
            blackIntervals = await DetectBlackIntervalsForCandidatesOrEmptyAsync(episode, scenes, threshold, minimum, minimumDuration, cancellationToken).ConfigureAwait(false);
            var supportedScenes = CreditSceneBuilder.DetectIntervalSupportedCreditScenes(blackFrames, blackIntervals, minimum, minimumDuration);
            if (supportedScenes.Count > 0)
            {
                var denseScenes = scenes
                    .Where(scene => !CreditSceneMetricsCalculator.Calculate(blackFrames, scene, minimum).IsSparse(scene, minimumDuration))
                    .ToList();
                scenes = [.. denseScenes
                    .Concat(supportedScenes.Where(supported => !denseScenes.Any(dense => supported.StartFrame <= dense.EndFrame && supported.EndFrame >= dense.StartFrame)))
                    .OrderBy(scene => scene.StartFrame)];
            }
        }

        // A dim last shot before the cut to the roll is black to the blackframe filter, and by the
        // statistics the scan keeps it is the same shape as a credit page on a lifted black. The
        // scene starts after such a lead-in, and the lead-in is a rejected range to the card run
        // finder, so its card-like keyframes cannot come back as a card run when what is left of the
        // scene is too short to be credits. A lighter section later in the scene stays.
        var rejected = new List<TimeRange>();
        var lastLighterKeyframes = new Dictionary<CreditScene, double>();
        if (visuals.Count > 0)
        {
            for (var i = 0; i < scenes.Count; i++)
            {
                var (trimmed, leadIn) = StartAfterDarkGreyLeadIn(scenes[i], blackFrames, minimum, visuals);
                if (leadIn is not { } range)
                {
                    continue;
                }

                rejected.Add(range);
                scenes[i] = trimmed;
                lastLighterKeyframes[trimmed] = range.End;
            }
        }

        // A roll or a dubbing card has lettering over black on most of its pages; a black gap between
        // acts, such as a cut to a commercial break, has it on none, and a cut followed by one dark
        // keyframe has it on half at most. A scene lettered on no more than half its pages is a gap.
        rejected.AddRange(scenes.Where(scene => visuals.Count > 0 && !IsMostlyLettered(scene, blackFrames, minimum, visuals)).Select(scene => new TimeRange(scene.StartTime, scene.EndTime)));
        scenes = [.. scenes.Where(scene => visuals.Count == 0 || IsMostlyLettered(scene, blackFrames, minimum, visuals))];
        if (scenes.Count == 0)
        {
            return (null, [], rejected);
        }

        foreach (var scene in RankCreditCandidates(scenes, blackIntervals))
        {
            // A trimmed scene starts at the first keyframe at the level, or where the lead-in probe
            // finds the frames before that keyframe already look like it. The gap before it is the
            // lead-in, black to the blackframe filter, so the boundary probe could only move the
            // start back into it.
            var refinedStartTime = !_config.RefineCreditsBoundary
                ? scene.StartTime
                : lastLighterKeyframes.TryGetValue(scene, out var lastLighterKeyframe)
                    ? await ProbeLeadInAsync(episode, lastLighterKeyframe, scene.StartTime, cancellationToken).ConfigureAwait(false)
                    : await RefineBoundaryAsync(episode, blackFrames, scene, sceneChange, threshold, minimumDuration, cancellationToken).ConfigureAwait(false);

            var segment = new Segment(
                episode.EpisodeId,
                new TimeRange(refinedStartTime + episode.CreditsFingerprintStart, scene.EndTime + episode.CreditsFingerprintStart));

            if (segment.Duration >= minimumDuration)
            {
                LogFoundValidCreditsSegment(segment.Start, segment.End, segment.Duration);

                // The picked scene carries its refined start, so the transition the boundary probe
                // confirmed counts as part of the roll for the card run.
                List<TimeRange> accepted = [.. scenes.Select(accepted => new TimeRange(accepted == scene ? refinedStartTime : accepted.StartTime, accepted.EndTime))];
                return (segment, accepted, rejected);
            }
        }

        return (null, [], rejected);
    }

    /// <summary>
    /// Drops the black percentage of keyframes whose visual is saturated. Black is unsaturated; a
    /// keyframe the blackframe filter counts as black at that saturation is a dark tinted scene, such
    /// as a blue night cave, not a roll or a card.
    /// </summary>
    private static List<BlackFrame> WithoutTintedKeyframes(List<BlackFrame> blackFrames, IReadOnlyList<KeyframeVisual> visuals)
    {
        if (visuals.Count == 0)
        {
            return blackFrames;
        }

        return [.. blackFrames.Select(frame => VisualAt(visuals, frame.Time) is { SaturationLow: >= CardRunFinder.BlackSaturationMaximum } ? frame with { Percentage = 0 } : frame)];
    }

    /// <summary>
    /// Moves a scene's start past its dark grey lead-in: the leading black keyframes that show dim
    /// content rather than black. The scene's black level is the median darkest tenth of its black
    /// keyframes, never below <see cref="LimitedRangeBlack"/>; a keyframe is dim when its darkest
    /// tenth sits more than <see cref="BlackLevelTolerance"/> above it, or, behind letterbox bars
    /// that pin the darkest tenth at black, when its 90th percentile sits above the level yet under
    /// the lettering contrast (see <see cref="IsDimContent"/>). A dim last shot before the cut to the
    /// roll is such a lead-in. So is the lighter prefix of a roll authored at two black levels:
    /// nothing the scan keeps tells the two apart, and the later start skips less story. A keyframe
    /// without a visual ends the lead-in, since nothing says it is dim. A scene whose lifted keyframes
    /// are the majority sets its level from their darkest tenth and keeps its start; the 90th
    /// percentile sets no level, so a dark majority behind bars is still a lead-in.
    /// </summary>
    /// <returns>The scene with its start moved and the lead-in from the old start to its last keyframe; the scene unchanged and <see langword="null"/> when there is no lead-in.</returns>
    private static (CreditScene Scene, TimeRange? LeadIn) StartAfterDarkGreyLeadIn(CreditScene scene, List<BlackFrame> blackFrames, int minimum, IReadOnlyList<KeyframeVisual> visuals)
    {
        var pages = new List<(BlackFrame Frame, KeyframeVisual? Visual)>();
        foreach (var frame in blackFrames)
        {
            if (frame.Percentage >= minimum
                && frame.Time >= scene.StartTime - CardRunFinder.KeyframeJoinTolerance
                && frame.Time <= scene.EndTime + CardRunFinder.KeyframeJoinTolerance)
            {
                pages.Add((frame, VisualAt(visuals, frame.Time)));
            }
        }

        List<double> levels = [.. pages.Select(page => page.Visual).OfType<KeyframeVisual>().Select(visual => visual.LumaLow).Order()];
        if (levels.Count == 0)
        {
            return (scene, null);
        }

        var blackLevel = Math.Max(LimitedRangeBlack, levels[levels.Count / 2]);
        var lastLeadInTime = scene.StartTime;
        foreach (var (frame, visual) in pages)
        {
            if (visual is null || !IsDimContent(visual, blackLevel))
            {
                return frame.Frame == scene.StartFrame
                    ? (scene, null)
                    : (new CreditScene(frame.Frame, scene.EndFrame, frame.Time, scene.EndTime), new TimeRange(scene.StartTime, lastLeadInTime));
            }

            lastLeadInTime = frame.Time;
        }

        return (scene, null);
    }

    /// <summary>
    /// Runs the lead-in probe for a nominated boundary: decodes the frames from the last lighter
    /// keyframe to just past the first at the level and places the start. The start is a function
    /// of the file between the two keyframes, so it is cached under them; a failed decode is not.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <param name="lastLighterKeyframe">A, relative to the credits fingerprint start.</param>
    /// <param name="firstLevelKeyframe">B, relative to the credits fingerprint start.</param>
    /// <param name="cancellationToken">Token used to cancel the decode.</param>
    /// <returns>The scene start relative to the credits fingerprint start: the located frame, or B when the frames before it do not match it or the window cannot be decoded.</returns>
    private async Task<double> ProbeLeadInAsync(QueuedEpisode episode, double lastLighterKeyframe, double firstLevelKeyframe, CancellationToken cancellationToken)
    {
        var offset = episode.CreditsFingerprintStart;
        var a = lastLighterKeyframe + offset;
        var b = firstLevelKeyframe + offset;

        double? start = null;
        if (_cacheService.TryRead(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.LeadIn, a, b, out double[] cached))
        {
            start = cached.Length > 0 ? cached[0] : null;
        }
        else if (await _ffmpegService.DecodeLumaWindowAsync(episode, LeadInProbe.ProbeWindow(a, b), LeadInProbe.Width, cancellationToken).ConfigureAwait(false) is { } frames)
        {
            start = LeadInProbe.Start(frames, a, b, BlackLevelTolerance);
            double[] located = start is { } time ? [time] : [];
            _cacheService.Write(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.LeadIn, a, b, located);
        }

        LogLeadInProbe(episode.Name, a, b, start ?? b);
        return (start ?? b) - offset;
    }

    // Dim content on a black keyframe: a background above the scene's black level, or a 90th
    // percentile above the level yet under the lettering contrast. The second reading is for a
    // picture behind letterbox bars, where the bars are the darkest tenth and stay at black however
    // dim the picture is; a dark scene there sits at 25 to 45 on the 90th percentile. A roll page
    // sits at the level on both, or lifts its 90th percentile onto the text when the lettering is
    // large. Pages dense with small lettering can land in between: inside a roll the rule never
    // reaches them, since it reads leading keyframes only, and a roll that opens on them starts after
    // them, the accepted trade: nothing the scan keeps tells such a page from a dark scene behind bars.
    private static bool IsDimContent(KeyframeVisual visual, double blackLevel)
    {
        var dimFloor = blackLevel + BlackLevelTolerance;
        return visual.LumaLow > dimFloor
            || (visual.LumaHigh > dimFloor && visual.LumaHigh < blackLevel + CardRunFinder.TextContrastMinimum);
    }

    // Only the scene's black keyframes count: an interval-supported scene can span keyframes that are
    // not black, such as the dark scene after a cut, and those must not vouch for it. A scene none of
    // whose black keyframes has a visual carries no evidence either way and stays.
    private static bool IsMostlyLettered(CreditScene scene, List<BlackFrame> blackFrames, int minimum, IReadOnlyList<KeyframeVisual> visuals)
    {
        var pages = 0;
        var lettered = 0;
        foreach (var frame in blackFrames)
        {
            if (frame.Percentage < minimum
                || frame.Time < scene.StartTime - CardRunFinder.KeyframeJoinTolerance
                || frame.Time > scene.EndTime + CardRunFinder.KeyframeJoinTolerance
                || VisualAt(visuals, frame.Time) is not { } visual)
            {
                continue;
            }

            pages++;
            if (CardRunFinder.IsLetteredPage(visual))
            {
                lettered++;
            }
        }

        return pages == 0 || lettered * 2 > pages;
    }

    // The blackframe and metadata filters format the same pts differently, so the two lists can sit
    // under a millisecond apart; visuals are ordered by time.
    private static KeyframeVisual? VisualAt(IReadOnlyList<KeyframeVisual> visuals, double time)
    {
        var low = 0;
        var high = visuals.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (visuals[mid].Time < time - CardRunFinder.KeyframeJoinTolerance)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low < visuals.Count && visuals[low].Time - time <= CardRunFinder.KeyframeJoinTolerance ? visuals[low] : null;
    }

    /// <summary>
    /// Runs targeted blackdetect scans for candidate ranges and converts failures into an empty result.
    /// </summary>
    /// <param name="episode">The episode being analyzed.</param>
    /// <param name="candidates">The candidate scenes that bound interval probes.</param>
    /// <param name="threshold">The FFmpeg blackdetect threshold.</param>
    /// <param name="minimum">The black-frame percentage threshold, passed through to blackdetect pic_th so interval confirmation uses the same definition of "black" as the keyframe pass.</param>
    /// <param name="minimumDuration">The minimum credit duration.</param>
    /// <param name="cancellationToken">The token used to cancel FFmpeg probing.</param>
    /// <returns>The detected black intervals, or an empty array when interval detection is unavailable.</returns>
    private async Task<BlackInterval[]> DetectBlackIntervalsForCandidatesOrEmptyAsync(
        QueuedEpisode episode,
        IReadOnlyList<CreditScene> candidates,
        int threshold,
        int minimum,
        int minimumDuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var intervals = new List<BlackInterval>();
            var (fingerprintStart, fingerprintEnd) = episode.GetFingerprintRange(AnalysisMode.Credits);
            foreach (var range in BuildIntervalProbeRanges(candidates, minimumDuration, fingerprintStart, fingerprintEnd))
            {
                intervals.AddRange(await _ffmpegService.DetectBlackIntervalsAsync(episode, range, threshold, minimum, cancellationToken).ConfigureAwait(false));
            }

            return [.. intervals];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogBlackIntervalDetectionUnavailable(ex, episode.Name);
            return [];
        }
    }

    /// <summary>
    /// Refines a scene start time when a targeted blackframe probe of the keyframe gap before the
    /// scene finds an earlier black transition. Probes only when the gap could change whether the
    /// scene reaches the minimum duration.
    /// </summary>
    /// <returns>The refined start time, or the original scene start when no valid refinement exists.</returns>
    private async Task<double> RefineBoundaryAsync(
        QueuedEpisode episode,
        List<BlackFrame> frames,
        CreditScene scene,
        int sceneChange,
        int threshold,
        int minimumDuration,
        CancellationToken cancellationToken)
    {
        var boundary = CreditsBoundaryHelper.FindBoundaryKeyframeTimes(frames, scene);
        if (boundary is null)
        {
            return scene.StartTime;
        }

        var (lastKeyframeTime, firstBlackTime) = boundary.Value;
        if (!CreditsBoundaryHelper.ShouldRefineBoundary(scene, lastKeyframeTime, minimumDuration))
        {
            return scene.StartTime;
        }

        var probeMinimum = CreditsBoundaryHelper.SelectProbeMinimum(frames, scene, sceneChange);
        var probeRange = new TimeRange(
            lastKeyframeTime + episode.CreditsFingerprintStart,
            firstBlackTime + episode.CreditsFingerprintStart);

        var probeFrames = await _ffmpegService
            .DetectBlackFramesAsync(episode, probeRange, probeMinimum, threshold, AnalysisMode.Credits, cancellationToken)
            .ConfigureAwait(false);

        if (probeFrames.Length == 0)
        {
            return scene.StartTime;
        }

        var refinedTime = CreditsBoundaryHelper.TryRefineBoundaryTime(probeFrames[0].Time, lastKeyframeTime, scene.StartTime);
        if (refinedTime is null)
        {
            return scene.StartTime;
        }

        LogRefinedBoundary(scene.StartTime, refinedTime.Value);
        return refinedTime.Value;
    }

    /// <summary>
    /// Builds bounded blackdetect probe ranges for candidate scenes.
    /// </summary>
    /// <param name="candidates">The candidate scenes that bound interval probes.</param>
    /// <param name="minimumDuration">The minimum credit duration, also used as probe padding on each side.</param>
    /// <param name="fingerprintStart">The absolute start of the credits fingerprint window.</param>
    /// <param name="fingerprintEnd">The absolute end of the credits fingerprint window.</param>
    /// <returns>The merged probe ranges clamped to the fingerprint window.</returns>
    internal static List<TimeRange> BuildIntervalProbeRanges(
        IReadOnlyList<CreditScene> candidates,
        int minimumDuration,
        double fingerprintStart,
        double fingerprintEnd)
    {
        var ranges = candidates
            .Select(candidate => new TimeRange(
                Math.Max(fingerprintStart, fingerprintStart + candidate.StartTime - minimumDuration),
                Math.Min(fingerprintEnd, fingerprintStart + candidate.EndTime + minimumDuration)))
            .Where(range => range.Duration > 0)
            .OrderBy(range => range.Start)
            .ToList();

        if (ranges.Count <= 1)
        {
            return ranges;
        }

        var merged = new List<TimeRange>(ranges.Count);
        var current = ranges[0];
        for (var i = 1; i < ranges.Count; i++)
        {
            var next = ranges[i];
            if (next.Start <= current.End)
            {
                current.End = Math.Max(current.End, next.End);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);
        return merged;
    }

    /// <summary>
    /// Ranks credit candidates, preferring scenes with interval support and then later scenes.
    /// </summary>
    /// <param name="scenes">The detected candidate scenes.</param>
    /// <param name="intervals">The blackdetect intervals available for scoring.</param>
    /// <returns>The ranked candidate scenes.</returns>
    internal static List<CreditScene> RankCreditCandidates(
        IReadOnlyList<CreditScene> scenes,
        IReadOnlyList<BlackInterval> intervals)
    {
        return [.. scenes
            .Select((scene, index) => new
            {
                Scene = scene,
                Index = index,
                HasIntervalSupport = HasIntervalSupport(scene, intervals),
            })
            .OrderByDescending(candidate => candidate.HasIntervalSupport)
            .ThenByDescending(candidate => candidate.Index)
            .Select(candidate => candidate.Scene)];
    }

    /// <summary>
    /// Determines whether a candidate scene overlaps a confirmed black interval.
    /// </summary>
    private static bool HasIntervalSupport(CreditScene scene, IReadOnlyList<BlackInterval> intervals)
    {
        foreach (var interval in intervals)
        {
            var overlapStart = Math.Max(scene.StartTime, interval.Start);
            var overlapEnd = Math.Min(scene.EndTime, interval.End);
            if (overlapEnd - overlapStart >= CreditDetectionPolicy.MinimumIntervalOverlapSeconds)
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found valid credits segment: start={Start:F2}s, end={End:F2}s, duration={Duration:F2}s")]
    private partial void LogFoundValidCreditsSegment(double start, double end, double duration);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refined credit boundary from {OriginalStart:F2}s to {RefinedStart:F2}s")]
    private partial void LogRefinedBoundary(double originalStart, double refinedStart);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Lead-in probe for {Episode} between {LastLighterKeyframe:F2}s and {FirstLevelKeyframe:F2}s starts the scene at {Start:F3}s")]
    private partial void LogLeadInProbe(string episode, double lastLighterKeyframe, double firstLevelKeyframe, double start);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Black interval detection unavailable for {Episode}")]
    private partial void LogBlackIntervalDetectionUnavailable(Exception ex, string episode);
}
