// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Decides what a dark grey lead-in before a black scene is, from a decoded window around the frame
/// where the background drops to the scene's black level.
/// </summary>
/// <remarks>
/// Pure over a <see cref="LumaWindow"/>, so the rules are tested without ffmpeg. The keyframe scan
/// nominates: A is the last keyframe lighter than the level, B the first at it. The probe locates
/// the level change L in (A, B], keeps the prefix when the same foreground stands on both sides of
/// L or when the last second before L is lettered pages, and otherwise trims to L. Anything it
/// cannot observe is inconclusive: the caller keeps B, the policy's start.
/// Every temporal rule is weighted by the frames' durations from their timestamps, clipped to the
/// span it measures; the last frame has no observed duration. Rows that never rise above the level
/// on more than a stray pixel are letterbox bars and leave every measure, since they would pin the
/// background at black however dim the picture is. The thresholds are experimental, measured on 24
/// episodes and synthetic clips, not validated defaults.
/// </remarks>
internal static class LeadInProbe
{
    // The window is decoded at this width. 320 px blurred small lettering into blobs; here text
    // scored 24 to 56 transitions per band row on the measured material and dark story 2 to 14.
    internal const int Width = 640;

    // Glyph transitions per band row that make a page lettered, between the highest dark story
    // window measured, 13.9, and the lowest text window, 24.1.
    internal const double TransitionsMinimum = 19;

    // What the rules need around the level change, with a margin for coverage: a second before it
    // for the text rule and half a second after it for stability. The change lies between two
    // keyframes, and the window spans them up to this gap: 9.5 s of 640 by 360 frames at 30 fps is
    // 285 frames, under the decode's 64 MiB cap.
    internal const double LookBackPadding = 1.25;
    internal const double LookAheadPadding = 0.75;
    internal const double MaximumKeyframeGap = 7.5;

    // A lettered page or a lit object covers at least 0.3 percent of the picture and at most 15.
    internal const double ForegroundMinimum = 0.003;
    internal const double ForegroundMaximum = 0.15;

    // The same page on both sides of the level change.
    internal const double ContinuityMinimumIou = 0.8;

    // The level must hold for half a second after L, on nine tenths of the observed time.
    internal const double StabilitySeconds = 0.5;
    internal const double StabilityMinimumFraction = 0.9;

    // The text rule reads the last second before L: three quarters of it must be observed and a
    // quarter of it must show foreground of lettering size.
    internal const double TextLookBackSeconds = 1.0;
    internal const double TextMinimumObservedSeconds = 0.75;
    internal const double TextMinimumForegroundSeconds = 0.25;

    // A row carries picture once this share of it rises above the level in some frame: a stray
    // pixel does not, a dim scene does.
    internal const double PictureRowMinimumFraction = 0.02;

    // Keyframe times come from the black-frame scan at millisecond precision, decoder times are finer.
    private const double TimeTolerance = 0.002;

    /// <summary>
    /// The window to decode for a nominated boundary.
    /// </summary>
    /// <param name="lastLighterKeyframe">A, in media time.</param>
    /// <param name="firstLevelKeyframe">B, in media time.</param>
    /// <returns>From <see cref="LookBackPadding"/> before A to <see cref="LookAheadPadding"/> after B, or <see langword="null"/> when the keyframes are further apart than <see cref="MaximumKeyframeGap"/>.</returns>
    internal static TimeRange? ProbeWindow(double lastLighterKeyframe, double firstLevelKeyframe)
        => firstLevelKeyframe - lastLighterKeyframe > MaximumKeyframeGap
            ? null
            : new TimeRange(lastLighterKeyframe - LookBackPadding, firstLevelKeyframe + LookAheadPadding);

    /// <summary>
    /// Finds the rows that carry picture: everything between the first and the last row that, in
    /// some frame of the window, has at least <see cref="PictureRowMinimumFraction"/> of its pixels
    /// above the level plus tolerance. The bands outside are letterbox bars. Black rows inside, the
    /// spacing between credit lines, stay picture, so a page's foreground is measured against the
    /// whole page. The fraction keeps a stray bright pixel, noise or ringing at a bar's edge, from
    /// turning a bar into picture.
    /// </summary>
    /// <param name="window">The decoded window.</param>
    /// <param name="blackLevel">The scene's black level on the scan's own scale.</param>
    /// <param name="tolerance">How far above the level a value still counts as black.</param>
    /// <returns>Per row, whether it carries picture.</returns>
    internal static bool[] PictureRows(LumaWindow window, double blackLevel, double tolerance)
    {
        var width = window.Width;
        var picture = new bool[window.Height];
        var minimumPixels = Math.Max(2, (int)Math.Ceiling(width * PictureRowMinimumFraction));
        for (var i = 0; i < window.FrameCount; i++)
        {
            var frame = window.Frame(i);
            for (var y = 0; y < picture.Length; y++)
            {
                if (picture[y])
                {
                    continue;
                }

                var above = 0;
                foreach (var value in frame.Slice(y * width, width))
                {
                    if (value > blackLevel + tolerance)
                    {
                        above++;
                    }
                }

                picture[y] = above >= minimumPixels;
            }
        }

        var first = Array.IndexOf(picture, true);
        if (first >= 0)
        {
            Array.Fill(picture, true, first, Array.LastIndexOf(picture, true) - first + 1);
        }

        return picture;
    }

    /// <summary>
    /// Measures one frame over its picture rows and writes its foreground mask. The background is
    /// the 10th percentile luma of the picture, the foreground every picture pixel at least the
    /// lettering contrast above it; rows outside the picture are never foreground. The foreground
    /// fraction is of the whole frame, since a page's own margins and letterbox bars look the same
    /// inside one window and the area thresholds were set on whole frames.
    /// </summary>
    /// <param name="frame">The frame's luma, row by row.</param>
    /// <param name="width">The frame width.</param>
    /// <param name="pictureRows">Per row, whether it carries picture.</param>
    /// <param name="mask">Receives, per pixel, whether it is foreground; as long as <paramref name="frame"/>.</param>
    /// <returns>The measure; all zero when no row carries picture.</returns>
    internal static LeadInFrameMeasure Measure(ReadOnlySpan<byte> frame, int width, ReadOnlySpan<bool> pictureRows, Span<bool> mask)
    {
        var height = frame.Length / width;
        Span<int> histogram = stackalloc int[256];
        var picturePixels = 0;
        for (var y = 0; y < height; y++)
        {
            if (!pictureRows[y])
            {
                continue;
            }

            foreach (var value in frame.Slice(y * width, width))
            {
                histogram[value]++;
            }

            picturePixels += width;
        }

        if (picturePixels == 0)
        {
            mask.Clear();
            return new LeadInFrameMeasure(0, 0, 0);
        }

        var background = Percentile10(histogram, picturePixels);
        var cutoff = background + CardRunFinder.TextContrastMinimum;
        var foreground = 0;
        var bandRows = 0;
        var transitions = 0;
        for (var y = 0; y < height; y++)
        {
            var rowMask = mask.Slice(y * width, width);
            if (!pictureRows[y])
            {
                rowMask.Clear();
                continue;
            }

            var row = frame.Slice(y * width, width);
            var rowForeground = 0;
            var rowTransitions = 0;
            var previous = false;
            for (var x = 0; x < width; x++)
            {
                var lit = row[x] >= cutoff;
                rowMask[x] = lit;
                if (lit)
                {
                    rowForeground++;
                }

                if (x > 0 && lit != previous)
                {
                    rowTransitions++;
                }

                previous = lit;
            }

            foreground += rowForeground;
            if (rowForeground >= 2)
            {
                bandRows++;
                transitions += rowTransitions;
            }
        }

        return new LeadInFrameMeasure(background, (double)foreground / frame.Length, bandRows == 0 ? 0 : (double)transitions / bandRows);
    }

    /// <summary>
    /// Intersection over union of two foreground masks.
    /// </summary>
    /// <param name="a">One mask.</param>
    /// <param name="b">The other mask, as long as <paramref name="a"/>.</param>
    /// <returns>Between 0 and 1; 0 when both are empty.</returns>
    internal static double Iou(ReadOnlySpan<bool> a, ReadOnlySpan<bool> b)
    {
        var both = 0;
        var either = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] && b[i])
            {
                both++;
            }

            if (a[i] || b[i])
            {
                either++;
            }
        }

        return either == 0 ? 0 : (double)both / either;
    }

    /// <summary>
    /// Decides what the lead-in is.
    /// </summary>
    /// <param name="window">The decoded window, covering (A, B] with context before and after.</param>
    /// <param name="lastLighterKeyframe">A, in media time.</param>
    /// <param name="firstLevelKeyframe">B, in media time.</param>
    /// <param name="blackLevel">The scene's black level on the scan's own scale.</param>
    /// <param name="tolerance">How far above the level a background still counts as black.</param>
    /// <returns>The decision.</returns>
    internal static LeadInDecision Decide(LumaWindow window, double lastLighterKeyframe, double firstLevelKeyframe, double blackLevel, double tolerance)
    {
        var count = window.FrameCount;
        if (count < 2)
        {
            return new LeadInDecision.Inconclusive("fewer than two frames decoded");
        }

        var pictureRows = PictureRows(window, blackLevel, tolerance);
        if (Array.IndexOf(pictureRows, true) < 0)
        {
            return new LeadInDecision.Inconclusive("no row rises above the level anywhere in the window");
        }

        var times = window.Times;
        var durations = new double[count];
        for (var i = 0; i < count - 1; i++)
        {
            durations[i] = times[i + 1] - times[i];
        }

        var frameSize = window.Width * window.Height;
        var scratch = new bool[frameSize];
        var measures = new LeadInFrameMeasure[count];
        var atLevel = new bool[count];
        for (var i = 0; i < count; i++)
        {
            measures[i] = Measure(window.Frame(i), window.Width, pictureRows, scratch);
            atLevel[i] = measures[i].Background <= blackLevel + tolerance;
        }

        // L: the first frame after A and not after B whose background is at the level and holds it,
        // and whose predecessor was above it. Without that crossing nothing changed between the
        // keyframes: the nomination came from a 90th percentile over a background that was already
        // black, and the first frame after A is not a cut.
        var located = -1;
        for (var i = 1; i < count && located < 0; i++)
        {
            if (times[i] > firstLevelKeyframe + TimeTolerance)
            {
                break;
            }

            if (times[i] <= lastLighterKeyframe + TimeTolerance || !atLevel[i] || atLevel[i - 1])
            {
                continue;
            }

            var (observed, held) = Observe(times, durations, atLevel, times[i], times[i] + StabilitySeconds);
            if (observed < StabilitySeconds - TimeTolerance)
            {
                return new LeadInDecision.Inconclusive("the window ends before the level held for half a second");
            }

            if (held >= StabilityMinimumFraction * observed)
            {
                located = i;
            }
        }

        if (located < 0)
        {
            // The background never left the level: the nomination read dim content in the 90th
            // percentile over a black background, and there is no frame to trim to. The second
            // before the last lighter keyframe, inside the prefix, still says whether that content
            // was lettered pages, which keeps the prefix; anything else is the policy's start.
            var anchor = -1;
            for (var i = 0; i < count && anchor < 0; i++)
            {
                if (times[i] >= lastLighterKeyframe - TimeTolerance)
                {
                    anchor = i;
                }
            }

            if (anchor <= 0 || Observe(times, durations, atLevel, times[anchor] - TextLookBackSeconds, times[anchor]).Observed < TextMinimumObservedSeconds - TimeTolerance)
            {
                return new LeadInDecision.Inconclusive("no background crossing, and less than three quarters of the second before the lighter keyframe was decoded");
            }

            return IsLetteredBefore(anchor, times, durations, measures)
                ? new LeadInDecision.Keep()
                : new LeadInDecision.Inconclusive("no background crossing to the level between the nominated keyframes");
        }

        // Continuity: the same foreground of lettering size on both sides of the change. A lit region
        // beyond the size of a page is story whatever survives the drop.
        if (measures[located - 1].ForegroundFraction is >= ForegroundMinimum and <= ForegroundMaximum
            && measures[located].ForegroundFraction is >= ForegroundMinimum and <= ForegroundMaximum)
        {
            var before = new bool[frameSize];
            var after = new bool[frameSize];
            Measure(window.Frame(located - 1), window.Width, pictureRows, before);
            Measure(window.Frame(located), window.Width, pictureRows, after);
            if (Iou(before, after) >= ContinuityMinimumIou)
            {
                return new LeadInDecision.Keep();
            }
        }

        var lookBackStart = times[located] - TextLookBackSeconds;
        var (observedBack, _) = Observe(times, durations, atLevel, lookBackStart, times[located]);
        if (observedBack < TextMinimumObservedSeconds - TimeTolerance)
        {
            return new LeadInDecision.Inconclusive("less than three quarters of the second before the level change was decoded");
        }

        return IsLetteredBefore(located, times, durations, measures)
            ? new LeadInDecision.Keep()
            : new LeadInDecision.TrimAt(times[located]);
    }

    // The text rule: over the second before frame `anchor`, frames whose foreground is lettering
    // size cover at least a quarter second and their duration-weighted median glyph transitions per
    // band row reach the threshold.
    private static bool IsLetteredBefore(int anchor, IReadOnlyList<double> times, double[] durations, LeadInFrameMeasure[] measures)
    {
        var lookBackStart = times[anchor] - TextLookBackSeconds;
        var lettered = new List<(double Transitions, double Seconds)>();
        for (var i = 0; i < anchor; i++)
        {
            var seconds = Overlap(times[i], durations[i], lookBackStart, times[anchor]);
            if (seconds > 0 && measures[i].ForegroundFraction is >= ForegroundMinimum and <= ForegroundMaximum)
            {
                lettered.Add((measures[i].TransitionsPerBandRow, seconds));
            }
        }

        return lettered.Sum(sample => sample.Seconds) >= TextMinimumForegroundSeconds
            && WeightedMedian(lettered) >= TransitionsMinimum;
    }

    // The 10th percentile of a luma histogram over the given number of pixels.
    private static int Percentile10(ReadOnlySpan<int> histogram, int pixels)
    {
        var target = (pixels + 9) / 10;
        var seen = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            seen += histogram[value];
            if (seen >= target)
            {
                return value;
            }
        }

        return 255;
    }

    // Observed time inside [from, to): every frame's duration clipped to the span, and the part of
    // it from frames at the level. The last frame has no next timestamp and observes nothing.
    private static (double Observed, double AtLevel) Observe(IReadOnlyList<double> times, double[] durations, bool[] atLevel, double from, double to)
    {
        double observed = 0;
        double held = 0;
        for (var i = 0; i < times.Count - 1; i++)
        {
            var seconds = Overlap(times[i], durations[i], from, to);
            observed += seconds;
            if (atLevel[i])
            {
                held += seconds;
            }
        }

        return (observed, held);
    }

    private static double Overlap(double start, double duration, double from, double to)
        => Math.Max(0, Math.Min(start + duration, to) - Math.Max(start, from));

    private static double WeightedMedian(List<(double Transitions, double Seconds)> samples)
    {
        samples.Sort((a, b) => a.Transitions.CompareTo(b.Transitions));
        var half = samples.Sum(sample => sample.Seconds) / 2;
        double seen = 0;
        foreach (var (transitions, seconds) in samples)
        {
            seen += seconds;
            if (seen >= half)
            {
                return transitions;
            }
        }

        return samples[^1].Transitions;
    }
}
