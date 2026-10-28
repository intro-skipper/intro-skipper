// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Detects credits from the keyframe scan as candidates for the credits pass: black rolls from
/// black-frame evidence, one for each scene that meets the minimum duration among the visually
/// verified scenes and the scenes after the last of them, or only the latest when none is verified,
/// and at most one card run from keyframe visuals.
/// </summary>
/// <remarks>
/// One decode reports both. Black-frame evidence is frame-accurate for credits on black and goes
/// through density gating and blackdetect interval recovery for sparse candidates. Two rules use the
/// visuals on black keyframes: a black scene starts after leading keyframes that show dim content
/// rather than black, such as a dim last shot before the cut to the roll, and a scene dim on every
/// black keyframe, or mostly a tinted dark picture after its lead-in, is dark story; and a black
/// scene lettered on no more than half its pages is a gap
/// between acts, not credits. A scene those rules accept ends at its
/// roll end: the last of the pages after it that are black or dark, colourless and lettered, since
/// dense lettering keeps roll pages under the black minimum and would split the roll. A scene's
/// range runs from its start to that end. With boundary refinement on, a scene or
/// the card run whose first page follows a cut starts on the cut frame between that page and the
/// page before (see <see cref="CutStart"/>), and an interval-supported scene keeps an interval start
/// that is earlier still. The gap is decoded for every such part the analyzer probes, and the start
/// is cached under the two keyframes. The cut start only refines: the minimum duration reads the
/// range before it moves the start, so it sets where an admitted part starts and never admits one.
/// Every visually verified scene whose range meets the minimum duration is a black-frame candidate,
/// so credits split by a mid-credits scene come out as separate parts. So is every scene after the
/// last verified one that meets it. Those scenes have no visual evidence because the scan reads black
/// frames to the end of the file but keeps visuals only inside the credits window, so they lie past a
/// window that ends early. A scene without visual evidence before a verified one is not a candidate.
/// With no verified scene, as on an ffmpeg without signalstats, only the latest scene that meets the
/// minimum duration is a candidate. The card run is found over each
/// page's card kind, which the analyzer sets from the black scenes those rules accepted and rejected.
/// Once they accept any scene, a black keyframe counts toward a card run only inside one (see
/// <see cref="StampCardKinds"/>).
/// </remarks>
/// <param name="logger">Logger for the analyzer.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="cacheService">Detection cache, for the cut start.</param>
/// <param name="configuration">Plugin configuration, or <see langword="null"/> to use the active plugin configuration.</param>
internal sealed partial class KeyframeAnalyzer(
    ILogger<KeyframeAnalyzer> logger,
    IFFmpegService ffmpegService,
    DetectionCacheService cacheService,
    PluginConfiguration? configuration = null)
{
    // Black sits at 16 on a limited-range source and lower on a full-range one, so 16 is the floor;
    // a scene whose black keyframes typically sit higher has lifted blacks and sets its own level. A
    // couple of levels over it is encoder noise, more is a dark grey scene.
    private const double LimitedRangeBlack = 16;
    private const double BlackLevelTolerance = 2;

    // Mean saturation (SATAVG, 0 to 255) under which a dark lettered page is a roll page.
    private const double RollSaturationMaximum = 0.25;

    // A dim keyframe with a mean saturation (SATAVG, 0 to 255) in this band and nothing at or over
    // this luma is a dark picture rather than lettering (see IsDarkPicture).
    private const double DarkPictureSaturationMinimum = 1;
    private const double DarkPictureSaturationMaximum = 8;
    private const double DarkPictureLumaMaximum = 200;

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
    /// <returns>Black-frame candidates under <see cref="SegmentSource.BlackFrame"/>, one for each scene whose range from its start before the cut start to its roll end meets the minimum duration among the visually verified scenes and the scenes after the last of them, or for only the latest such scene when none is verified, and at most one card run under <see cref="SegmentSource.KeyframeVisuals"/>. Failures of the keyframe scan propagate to the credits pass, which drops the keyframe candidates; a failed interval probe or cut decode is logged and falls back.</returns>
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
    /// <returns>Black-frame candidates under <see cref="SegmentSource.BlackFrame"/>, one for each scene whose range from its start before the cut start to its roll end meets the minimum duration among the visually verified scenes and the scenes after the last of them, or for only the latest such scene when none is verified, and at most one card run under <see cref="SegmentSource.KeyframeVisuals"/>.</returns>
    internal async Task<IReadOnlyList<AttributedSegment>> DetectCreditsAsync(QueuedEpisode episode, int minimumPercentage, int threshold, int minimumDuration, bool detectCardCredits, CancellationToken cancellationToken = default)
    {
        // On an ffmpeg without signalstats no page has a visual, which leaves the gates on black
        // keyframes inert.
        var pages = await _ffmpegService.ScanKeyframesAsync(episode, threshold, cancellationToken).ConfigureAwait(false);

        // The black-frame rules read the pages' rows through this list, index-aligned with the pages.
        List<BlackFrame> sceneFrames = [.. pages.Select(page => page.Frame)];
        var (blackMinimum, sceneChange) = sceneFrames.Count > 0
            ? BlackFrameThresholdHelper.NormalizeThreshold(sceneFrames, minimumPercentage)
            : (minimumPercentage, minimumPercentage);
        var (credits, scenes, rejected) = sceneFrames.Count > 0
            ? await DetectBlackFrameCreditsAsync(episode, sceneFrames, pages, blackMinimum, sceneChange, threshold, minimumDuration, cancellationToken).ConfigureAwait(false)
            : ([], [], []);

        List<AttributedSegment> candidates = [.. credits.Select(segment => new AttributedSegment(segment, SegmentSource.BlackFrame))];
        if (detectCardCredits)
        {
            var range = CardRunFinder.FindCreditRange(StampCardKinds(pages, blackMinimum, scenes, rejected), minimumDuration);
            if (range is not null)
            {
                // The run starts on one of the pages, so its first page is found by time.
                var start = _config.RefineCreditsBoundary
                    ? await CutStartAsync(episode, pages, Array.FindIndex(pages, page => page.Frame.Time == range.Start), cancellationToken).ConfigureAwait(false)
                    : range.Start;
                candidates.Add(new AttributedSegment(
                    new Segment(episode.EpisodeId, new TimeRange(start + episode.CreditsFingerprintStart, range.End + episode.CreditsFingerprintStart)),
                    SegmentSource.KeyframeVisuals));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Stamps each page that has a visual with its card kind, from the black scenes the black-frame
    /// rules accepted and rejected. The rules apply in order, with membership by
    /// <see cref="InScene"/> on the page's time:
    /// <list type="number">
    /// <item><description>A solid white page is content.</description></item>
    /// <item><description>A page that is black by the raw percentage and in a rejected range is content.</description></item>
    /// <item><description>When accepted is empty, a card-like page is a card and any other page is content.</description></item>
    /// <item><description>A black or card-like page in an accepted scene is a black card.</description></item>
    /// <item><description>A card-like page that is not black is a card.</description></item>
    /// <item><description>Any other page is content.</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The accepted and rejected ranges are the only scene evidence the kinds read, and the
    /// accepted scenes carry their interval evidence and cut starts. A probed scene that falls short of
    /// the minimum duration is still accepted while another meets it, so its pages stay black cards.
    /// A scene that meets the minimum starts at its refined start, and a short one at its keyframe.
    /// Inside an accepted scene a blank black page between two roll pages is a black card, so it
    /// does not break the roll. A card-like page inside an accepted scene is a black card too, so a
    /// vanity card between two roll parts cannot give the roll a card density of its own. Both
    /// extend the run and count toward its duration, so short white cards and a short roll qualify
    /// together, but neither counts toward its density. Counting black cards toward density let a
    /// black roll carry scattered flat shots before it into the run, which admitted 67 seconds of
    /// story on an anime epilogue. Once any scene is accepted, a black page outside every accepted
    /// scene is content, since the black-frame rules turned it down, as they do a dark lead-in
    /// before the roll's transition or a black flash before an interval-confirmed roll. A rejected
    /// range turns only black pages into content. A card-like page below the black threshold there
    /// is a card, or a black card when an accepted scene also holds it. With no accepted scene the
    /// visuals alone decide, as the old fallback did, so black cards the black-frame rules could
    /// not confirm still count unless they were rejected.
    /// </remarks>
    /// <param name="pages">The keyframe scan's pages, ordered by time, with their raw black percentages. A page without a visual gets no kind.</param>
    /// <param name="blackMinimum">The black percentage at or above which a page is black, normalized against the scan by the black-frame rules.</param>
    /// <param name="accepted">Every black scene the black-frame rules accepted, from its refined start to its roll end, relative to the credits fingerprint start; empty when no scene met the minimum duration.</param>
    /// <param name="rejected">The lead-ins and the scenes the lettering gate rejected as gaps, relative to the credits fingerprint start.</param>
    /// <returns>One card page for each page that has a visual, in page order.</returns>
    internal static List<CardPage> StampCardKinds(IReadOnlyList<KeyframePage> pages, int blackMinimum, IReadOnlyList<TimeRange> accepted, IReadOnlyList<TimeRange> rejected)
    {
        List<CardPage> kinds = [];
        foreach (var (frame, visual) in pages)
        {
            if (visual is null)
            {
                continue;
            }

            var time = frame.Time;
            var black = frame.Percentage >= blackMinimum;
            var cardLike = visual.IsCardLike();
            var kind = visual.IsSolidWhite() || (black && rejected.Any(range => InScene(range.Start, range.End, time))) ? CardKind.Content
                : accepted.Count == 0 ? (cardLike ? CardKind.Card : CardKind.Content)
                : (black || cardLike) && accepted.Any(scene => InScene(scene.Start, scene.End, time)) ? CardKind.BlackCard
                : cardLike && !black ? CardKind.Card
                : CardKind.Content;
            kinds.Add(new CardPage(time, kind));
        }

        return kinds;
    }

    /// <summary>
    /// Detects credits from black-frame keyframe evidence, with optional blackdetect interval recovery and cut starts.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="blackFrames">The keyframe black-frame scan results.</param>
    /// <param name="pages">The keyframe scan's pages, index-aligned with <paramref name="blackFrames"/>.</param>
    /// <param name="minimum">The black percentage at or above which a keyframe is black, normalized against the scan.</param>
    /// <param name="sceneChange">The black percentage that marks the transition into credits, normalized against the scan.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="minimumDuration">Minimum duration of the credits.</param>
    /// <param name="cancellationToken">Token used to cancel FFmpeg probing.</param>
    /// <returns>The black-frame candidates in file time, one for each probed scene whose range from its start before the cut start to its roll end meets the minimum duration, starting at its refined start; every probed scene, which is each verified scene and every scene after the last of them, or with none verified the scenes walked latest first down to the candidate, relative to the credits fingerprint start and each with its refined start and roll end, or an empty list when no scene met the minimum duration; and the lead-ins and the scenes the lettering gate rejected as gaps.</returns>
    private async Task<(List<Segment> Credits, List<TimeRange> Scenes, List<TimeRange> Rejected)> DetectBlackFrameCreditsAsync(QueuedEpisode episode, List<BlackFrame> blackFrames, IReadOnlyList<KeyframePage> pages, int minimum, int sceneChange, int threshold, int minimumDuration, CancellationToken cancellationToken)
    {
        var scenes = CreditSceneBuilder.DetectCreditScenes(blackFrames, minimum, sceneChange, minimumDuration);
        if (scenes.Count == 0)
        {
            var candidates = CreditSceneBuilder.FindRawScenes(blackFrames, minimum);
            if (candidates.Count == 0)
            {
                return ([], [], []);
            }

            var blackIntervals = await DetectBlackIntervalsForCandidatesOrEmptyAsync(episode, candidates, threshold, minimum, minimumDuration, cancellationToken).ConfigureAwait(false);
            scenes = CreditSceneBuilder.DetectIntervalSupportedCreditScenes(blackFrames, candidates, blackIntervals, minimum, minimumDuration);
            if (scenes.Count == 0)
            {
                return ([], [], []);
            }
        }
        else if (scenes.Any(scene => CreditSceneMetricsCalculator.Calculate(blackFrames, scene, minimum).IsSparse(scene, minimumDuration)))
        {
            // Run the blackdetect interval probe over every scene. When it confirms any run, keep
            // dense scenes unchanged and add the scenes the intervals make of the runs outside them.
            // If it confirms none, keep the scenes as they are.
            var blackIntervals = await DetectBlackIntervalsForCandidatesOrEmptyAsync(episode, scenes, threshold, minimum, minimumDuration, cancellationToken).ConfigureAwait(false);
            var runs = CreditSceneBuilder.FindRawScenes(blackFrames, minimum);
            if (CreditSceneBuilder.DetectIntervalSupportedCreditScenes(blackFrames, runs, blackIntervals, minimum, minimumDuration).Count > 0)
            {
                var denseScenes = scenes
                    .Where(scene => !CreditSceneMetricsCalculator.Calculate(blackFrames, scene, minimum).IsSparse(scene, minimumDuration))
                    .ToList();
                List<CreditScene> runsOutsideDenseScenes = [.. runs.Where(run => !denseScenes.Any(dense => run.StartIndex <= dense.EndIndex && run.EndIndex >= dense.StartIndex))];
                scenes = [.. denseScenes
                    .Concat(CreditSceneBuilder.DetectIntervalSupportedCreditScenes(blackFrames, runsOutsideDenseScenes, blackIntervals, minimum, minimumDuration))
                    .OrderBy(scene => scene.StartIndex)];
            }
        }

        // A dim last shot before the cut to the roll is black to the blackframe filter, and by the
        // statistics the scan keeps it is the same shape as a credit page on a lifted black. The
        // scene starts after such a lead-in, and the lead-in is a rejected range to the card kinds, so
        // its card-like keyframes cannot come back as a card run when what is left of the scene is too
        // short to be credits. A lighter section later in the scene stays. A scene that is lead-in to
        // its end, such as a dim night scene well before the roll, goes whole, and so does a scene
        // that is mostly a tinted dark picture after its lead-in.
        var rejected = new List<TimeRange>();
        for (var i = scenes.Count - 1; i >= 0; i--)
        {
            var (trimmed, leadIn) = StartAfterDarkGreyLeadIn(scenes[i], blackFrames, minimum, pages);
            if (leadIn is not { } range)
            {
                continue;
            }

            rejected.Add(range);
            if (trimmed is null)
            {
                scenes.RemoveAt(i);
                continue;
            }

            scenes[i] = trimmed;
        }

        // A roll or a dubbing card has lettering over black on most of its pages; a black gap between
        // acts, such as a cut to a commercial break, has it on none, and a cut followed by one dark
        // keyframe has it on half at most. A scene lettered on no more than half its pages is a gap.
        var lettering = scenes.ToDictionary(scene => scene, scene => IsMostlyLettered(scene, blackFrames, minimum, pages));
        rejected.AddRange(scenes.Where(scene => lettering[scene] is false).Select(scene => new TimeRange(scene.StartTime, scene.EndTime)));
        scenes = [.. scenes.Where(scene => lettering[scene] is not false)];
        if (scenes.Count == 0)
        {
            return ([], [], rejected);
        }

        // Every verified scene whose range meets the minimum is a candidate, and so is every scene
        // after the last verified one, none of which has a verdict. A scene without a verdict before
        // a verified one is not probed. With no verified scene, the scenes are walked latest first and
        // the first whose range meets the minimum is the only candidate.
        List<Segment> credits = [];
        List<TimeRange> accepted = [];
        var lastVerified = scenes.FindLastIndex(scene => lettering[scene] is true);
        var scenesToProbe = scenes.Where(scene => lettering[scene] is true).Concat(scenes.Skip(lastVerified + 1).Reverse());
        foreach (var scene in scenesToProbe)
        {
            // Every probed scene runs to its roll end, and the card kinds read its range: a black or
            // card-like page in it is a black card, which covers black pages among the roll pages. A
            // roll page itself is neither and stays content.
            var endTime = RollEnd(scene, blackFrames, minimum, pages);

            // The minimum reads the range before the cut start, so the frames the cut start adds
            // before the first page never lift a short scene over it, and a short scene is not
            // decoded. Its start stays at the keyframe: no page lies between the cut and that
            // keyframe, so the earlier start would change no card kind.
            if (endTime - scene.StartTime < minimumDuration)
            {
                accepted.Add(new TimeRange(scene.StartTime, endTime));
                continue;
            }

            // The first page is the start frame: an interval-supported scene starts on blackdetect's
            // clock, where no page may lie, and keeps that start when it is earlier than the cut.
            var refinedStartTime = _config.RefineCreditsBoundary
                ? Math.Min(scene.StartTime, await CutStartAsync(episode, pages, scene.StartIndex, cancellationToken).ConfigureAwait(false))
                : scene.StartTime;
            accepted.Add(new TimeRange(refinedStartTime, endTime));

            var segment = new Segment(
                episode.EpisodeId,
                new TimeRange(refinedStartTime + episode.CreditsFingerprintStart, endTime + episode.CreditsFingerprintStart));
            LogFoundValidCreditsSegment(segment.Start, segment.End, segment.Duration);
            credits.Add(segment);
            if (lastVerified < 0)
            {
                break;
            }
        }

        // With no candidate no scene is accepted, and the card kinds fall back to the visuals.
        return (credits, credits.Count > 0 ? accepted : [], rejected);
    }

    /// <summary>
    /// Moves a scene's start past its dark grey lead-in: the leading black keyframes that show dim
    /// content rather than black. The scene's black level is the median darkest tenth of its black
    /// keyframes, never below <see cref="LimitedRangeBlack"/>; a keyframe is dim when its darkest
    /// tenth sits more than <see cref="BlackLevelTolerance"/> above it, or when its 90th percentile
    /// sits above the level yet under the lettering contrast, as a dim picture's does whether letterbox
    /// bars pin its darkest tenth at black or the picture sets the level itself (see
    /// <see cref="IsDimContent"/>). A dim last shot before the cut to the roll is such a lead-in. So is
    /// the lighter prefix of a roll authored at two black levels: nothing the scan keeps tells the two
    /// apart, and the later start skips less story. A keyframe without a visual ends the lead-in, since
    /// nothing says it is dim. A scene whose lifted keyframes are the majority sets its level from
    /// their darkest tenth and keeps its start unless their 90th percentile is dim too; the 90th
    /// percentile sets no level, so a dark majority is still a lead-in, bars or not. A scene dim on
    /// every black keyframe is lead-in to its end: none of its keyframes reads as black, so it is dark
    /// story, not credits. So is a scene whose keyframes from where the lead-in stops are mostly a
    /// dark picture (see <see cref="IsDarkPicture"/>): a night scene can open on a keyframe at the
    /// black level, which ends no lead-in. The black keyframes are read from 10 ms before the start (see
    /// <see cref="InScene"/>). A scene that a blackdetect interval confirmed starts on blackdetect's
    /// clock, and its cut keyframe can sit just before that start on the scan's clock. The first
    /// keyframe read counts as the scene's first, whether or not it is the start frame. Stopping on it
    /// leaves no lead-in, and a lead-in starts at it when it is earlier than the scene's start.
    /// </summary>
    /// <returns>The scene with its start moved and the lead-in from the earlier of the old start and the first keyframe read to the lead-in's last keyframe; no scene and that lead-in, ending on the last keyframe read, when every keyframe read is dim or most of those after the lead-in are a dark picture; the scene unchanged and <see langword="null"/> when there is no lead-in.</returns>
    private static (CreditScene? Scene, TimeRange? LeadIn) StartAfterDarkGreyLeadIn(CreditScene scene, List<BlackFrame> blackFrames, int minimum, IReadOnlyList<KeyframePage> pages)
    {
        List<(int Index, BlackFrame Frame, KeyframeVisual? Visual)> scenePages = [];
        for (var i = 0; i < blackFrames.Count; i++)
        {
            var frame = blackFrames[i];
            if (frame.Percentage >= minimum && InScene(scene.StartTime, scene.EndTime, frame.Time))
            {
                scenePages.Add((i, frame, pages[i].Visual));
            }
        }

        List<double> levels = [.. scenePages.Select(page => page.Visual).OfType<KeyframeVisual>().Select(visual => visual.LumaLow).Order()];
        if (levels.Count == 0)
        {
            return (scene, null);
        }

        var blackLevel = Math.Max(LimitedRangeBlack, levels[levels.Count / 2]);
        var firstPage = scenePages[0].Frame;
        var lastLeadInTime = scene.StartTime;
        for (var k = 0; k < scenePages.Count; k++)
        {
            var (index, frame, visual) = scenePages[k];
            if (visual is null || !IsDimContent(visual, blackLevel))
            {
                List<KeyframeVisual> rest = [.. scenePages.Skip(k).Select(page => page.Visual).OfType<KeyframeVisual>()];
                if (rest.Count(page => IsDarkPicture(page, blackLevel)) * 2 > rest.Count)
                {
                    return (null, new TimeRange(Math.Min(firstPage.Time, scene.StartTime), scenePages[^1].Frame.Time));
                }

                return k == 0
                    ? (scene, null)
                    : (new CreditScene(index, scene.EndIndex, frame.Time, scene.EndTime), new TimeRange(Math.Min(firstPage.Time, scene.StartTime), lastLeadInTime));
            }

            lastLeadInTime = frame.Time;
        }

        return (null, new TimeRange(Math.Min(firstPage.Time, scene.StartTime), lastLeadInTime));
    }

    /// <summary>
    /// Finds where a part whose first page is the page at the index starts: on the cut frame of the
    /// keyframe gap before it (see <see cref="CutStart"/>), or on the page. The start is a function of
    /// the file between the two keyframes, so it is cached under them; a failed decode is not.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <param name="pages">The keyframe scan's pages.</param>
    /// <param name="index">The part's first page.</param>
    /// <param name="cancellationToken">Token used to cancel the decode.</param>
    /// <returns>The start relative to the credits fingerprint start: the cut frame, or the page's time when the page follows no cut, no frame carries the cut, or the gap cannot be decoded.</returns>
    private async Task<double> CutStartAsync(QueuedEpisode episode, IReadOnlyList<KeyframePage> pages, int index, CancellationToken cancellationToken)
    {
        var page = pages[index];
        if (CutStart.Window(pages, index) is not { } gap || page.Visual is not { } visual)
        {
            return page.Frame.Time;
        }

        var offset = episode.CreditsFingerprintStart;
        var window = new TimeRange(gap.Start + offset, gap.End + offset);
        double? start = null;
        if (_cacheService.TryRead(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.Cut, window.Start, window.End, out double[] cached))
        {
            start = cached.Length > 0 ? cached[0] : null;
        }
        else if (await _ffmpegService.DecodeCutWindowAsync(episode, window, cancellationToken).ConfigureAwait(false) is { } frames)
        {
            start = CutStart.Locate(frames, window, visual.FrameDifference);
            double[] located = start is { } time ? [time] : [];
            _cacheService.Write(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.Cut, window.Start, window.End, located);
        }

        LogCutStart(episode.Name, window.Start, window.End, start ?? window.End);
        return start is { } cut ? cut - offset : page.Frame.Time;
    }

    // Dim content on a black keyframe: a background above the scene's black level, or a 90th
    // percentile above the level yet under the lettering contrast. The second reading catches a dim
    // picture whose darkest tenth sits at the level: behind letterbox bars the bars are the darkest
    // tenth and stay at black however dim the picture is, and a dark scene without bars sets the
    // level itself. Either way a dark scene sits at 25 to 45 on the 90th percentile. A roll page
    // sits at the level on both, or lifts its 90th percentile onto the text when the lettering is
    // large. Pages dense with small lettering can land in between: inside a roll the trim never
    // reaches them, since it reads leading keyframes only, a roll that opens on them starts after
    // them, and a roll of nothing else is rejected whole. This test alone does not tell such a page
    // from a dark scene; IsDarkPicture adds the tint. After an accepted scene they are roll pages,
    // and the scene's roll end takes them back (see RollEnd).
    private static bool IsDimContent(KeyframeVisual visual, double blackLevel)
    {
        var dimFloor = blackLevel + BlackLevelTolerance;
        return visual.LumaLow > dimFloor
            || (visual.LumaHigh > dimFloor && visual.LumaHigh < blackLevel + KeyframeVisualTraits.TextContrastMinimum);
    }

    // A dark picture on a black keyframe: dim content with a faint tint and nothing near white. A
    // page dense with small lettering is dim by the same test, but its lettering is near white,
    // colourless (grey text measured under 0.9) or strongly coloured (coloured lettering and logos
    // over 10). On the measured corpus a credits part had at most a twenty-fifth of its black
    // keyframes like this, and a dark story scene the blackframe filter reads as black had most.
    // An HDR source is scanned without tone mapping, so its white lettering reads under 200 and
    // only the tint band protects it.
    private static bool IsDarkPicture(KeyframeVisual visual, double blackLevel)
        => IsDimContent(visual, blackLevel)
            && visual.Saturation >= DarkPictureSaturationMinimum
            && visual.Saturation < DarkPictureSaturationMaximum
            && visual.LumaMax < DarkPictureLumaMaximum;

    // A roll page: black at the darkest tenth, colourless and lettered. Dense lettering takes roll
    // pages to 70 to 87 percent black, under the minimum, so black pages can lie more than 20 s
    // apart and split the roll into scenes. On the measured rolls these pages sit at a mean
    // saturation of 0 and the inset story footage beside them at 0.39 or more. Lettered is the
    // contrast test, so a colourless dark shot with one bright highlight passes too.
    private static bool IsRollPage(KeyframeVisual visual)
        => visual.LumaLow <= LimitedRangeBlack + BlackLevelTolerance
            && visual.Saturation < RollSaturationMaximum
            && visual.IsLettered();

    // Where an accepted scene ends: at the last of the pages after its last black keyframe that are
    // black or roll pages, walked page by page from that keyframe. The walk starts at the end index,
    // not the end time, which for an interval-supported scene can lie past its last black
    // keyframe. A page without a visual stops it, so it never leaves the credits window and is
    // inert on an ffmpeg without signalstats.
    private static double RollEnd(CreditScene scene, List<BlackFrame> blackFrames, int minimum, IReadOnlyList<KeyframePage> pages)
    {
        var last = scene.EndIndex;
        while (last + 1 < pages.Count
            && pages[last + 1].Visual is { } visual
            && (blackFrames[last + 1].Percentage >= minimum || IsRollPage(visual)))
        {
            last++;
        }

        return Math.Max(scene.EndTime, blackFrames[last].Time);
    }

    // Only the scene's black keyframes count: an interval-supported scene can span keyframes that are
    // not black, such as the dark scene after a cut, and those must not vouch for it. A scene none of
    // whose black keyframes has a visual carries no evidence either way and gets no verdict.
    private static bool? IsMostlyLettered(CreditScene scene, List<BlackFrame> blackFrames, int minimum, IReadOnlyList<KeyframePage> pages)
    {
        var withVisual = 0;
        var lettered = 0;
        for (var i = 0; i < blackFrames.Count; i++)
        {
            var frame = blackFrames[i];
            if (frame.Percentage < minimum
                || !InScene(scene.StartTime, scene.EndTime, frame.Time)
                || pages[i].Visual is not { } visual)
            {
                continue;
            }

            withVisual++;
            if (visual.IsLettered())
            {
                lettered++;
            }
        }

        return withVisual == 0 ? null : lettered * 2 > withVisual;
    }

    // Whether a keyframe at the time belongs to the scene from start to end. The lead-in and
    // lettering gates and the card kinds all use it. A keyframe within the pad of a scene's bounds
    // belongs to the scene: an interval-supported scene starts on blackdetect's clock, and its cut
    // keyframe can sit just before that start on the keyframe scan's clock.
    private static bool InScene(double start, double end, double time)
    {
        const double pad = 0.01;
        return time >= start - pad && time <= end + pad;
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
    /// Builds bounded blackdetect probe ranges for candidate scenes.
    /// </summary>
    /// <param name="candidates">The candidate scenes that bound interval probes.</param>
    /// <param name="minimumDuration">The minimum credit duration, also used as probe padding on each side.</param>
    /// <param name="fingerprintStart">The absolute start of the credits fingerprint window.</param>
    /// <param name="fingerprintEnd">The absolute end of the credits fingerprint window.</param>
    /// <returns>The merged probe ranges clamped to the fingerprint window.</returns>
    private static List<TimeRange> BuildIntervalProbeRanges(
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

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found valid credits segment: start={Start:F2}s, end={End:F2}s, duration={Duration:F2}s")]
    private partial void LogFoundValidCreditsSegment(double start, double end, double duration);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cut start for {Episode} between the keyframes at {PageBefore:F2}s and {Page:F2}s is {Start:F3}s")]
    private partial void LogCutStart(string episode, double pageBefore, double page, double start);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Black interval detection unavailable for {Episode}")]
    private partial void LogBlackIntervalDetectionUnavailable(Exception ex, string episode);
}
