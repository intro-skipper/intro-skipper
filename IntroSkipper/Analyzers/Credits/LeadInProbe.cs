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
/// cannot observe is inconclusive: the caller keeps B, the policy's start, and caches nothing.
/// Every temporal rule is weighted by the frames' durations from their timestamps, clipped to the
/// span it measures; the last frame has no observed duration. The thresholds are experimental,
/// measured on one sample and synthetic clips, not validated defaults.
/// </remarks>
internal static class LeadInProbe
{
    // A lettered page or a lit object covers at least 0.3 percent of the frame and at most 15.
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

    // Keyframe times come from the black-frame scan at millisecond precision, decoder times are finer.
    private const double TimeTolerance = 0.002;

    /// <summary>
    /// Glyph transitions per band row that make a page lettered, read per resolution and not
    /// scaled: 18 at 320 px and 22 at 640 px on the measured material, where dark story reached
    /// 10.7 and 13.9 and text started at 22.1 and 28.5.
    /// </summary>
    /// <param name="width">The decoded frame width.</param>
    /// <returns>The threshold.</returns>
    internal static double TransitionsThreshold(int width) => width <= 400 ? 18 : 22;

    /// <summary>
    /// Measures one frame and writes its foreground mask.
    /// </summary>
    /// <param name="frame">The frame's luma, row by row.</param>
    /// <param name="width">The frame width.</param>
    /// <param name="mask">Receives, per pixel, whether it is foreground; as long as <paramref name="frame"/>.</param>
    /// <returns>The measure.</returns>
    internal static LeadInFrameMeasure Measure(ReadOnlySpan<byte> frame, int width, Span<bool> mask)
    {
        var background = Percentile10(frame);
        var cutoff = background + CardRunFinder.TextContrastMinimum;
        var height = frame.Length / width;
        var foreground = 0;
        var bandRows = 0;
        var transitions = 0;
        for (var y = 0; y < height; y++)
        {
            var row = frame.Slice(y * width, width);
            var rowMask = mask.Slice(y * width, width);
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
            measures[i] = Measure(window.Frame(i), window.Width, scratch);
            atLevel[i] = measures[i].Background <= blackLevel + tolerance;
        }

        // L: the first frame after A and not after B whose background is at the level and holds it.
        var located = -1;
        for (var i = 0; i < count && located < 0; i++)
        {
            if (times[i] <= lastLighterKeyframe + TimeTolerance || times[i] > firstLevelKeyframe + TimeTolerance || !atLevel[i])
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
            return new LeadInDecision.Inconclusive("no level change located between the nominated keyframes");
        }

        if (located == 0)
        {
            return new LeadInDecision.Inconclusive("the window starts at the level change");
        }

        // Continuity: the same foreground of lettering size on both sides of the change. A lit region
        // beyond the size of a page is story whatever survives the drop.
        if (measures[located - 1].ForegroundFraction is >= ForegroundMinimum and <= ForegroundMaximum
            && measures[located].ForegroundFraction is >= ForegroundMinimum and <= ForegroundMaximum)
        {
            var before = new bool[frameSize];
            var after = new bool[frameSize];
            Measure(window.Frame(located - 1), window.Width, before);
            Measure(window.Frame(located), window.Width, after);
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

        var lettered = new List<(double Transitions, double Seconds)>();
        for (var i = 0; i < located; i++)
        {
            var seconds = Overlap(times[i], durations[i], lookBackStart, times[located]);
            if (seconds > 0 && measures[i].ForegroundFraction is >= ForegroundMinimum and <= ForegroundMaximum)
            {
                lettered.Add((measures[i].TransitionsPerBandRow, seconds));
            }
        }

        if (lettered.Sum(sample => sample.Seconds) >= TextMinimumForegroundSeconds
            && WeightedMedian(lettered) >= TransitionsThreshold(window.Width))
        {
            return new LeadInDecision.Keep();
        }

        return new LeadInDecision.TrimAt(times[located]);
    }

    // The 10th percentile luma of a frame, from a histogram.
    private static int Percentile10(ReadOnlySpan<byte> frame)
    {
        Span<int> histogram = stackalloc int[256];
        foreach (var value in frame)
        {
            histogram[value]++;
        }

        var target = (frame.Length + 9) / 10;
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
