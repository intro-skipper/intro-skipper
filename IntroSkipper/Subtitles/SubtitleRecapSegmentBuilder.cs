// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Subtitles;

/// <summary>
/// Builds a recap <see cref="SubtitleRecapResult"/> from parsed subtitle cues by locating a
/// recap-opening phrase, growing a dense cue cluster, and optionally snapping the end to a black
/// frame. This is a pure function of its inputs and carries no I/O, so it is fully unit-testable
/// without media.
/// </summary>
public static class SubtitleRecapSegmentBuilder
{
    /// <summary>
    /// Builds a recap segment from subtitle cues.
    /// </summary>
    /// <param name="cues">Parsed subtitle cues (any order; sorted internally by start time).</param>
    /// <param name="matcher">The recap-opening phrase matcher.</param>
    /// <param name="options">Builder tunables.</param>
    /// <param name="blackFrameTimes">
    /// Optional black-frame timestamps (in seconds) from the existing ffmpeg black-frame pass, used to
    /// snap the recap end to the fade-out. Pass <see langword="null"/> or empty to skip snapping.
    /// </param>
    /// <returns>The recap segment, or <see langword="null"/> when no recap was found or it failed validation.</returns>
    public static SubtitleRecapResult? Build(
        IReadOnlyList<SubtitleCue> cues,
        RecapPhraseMatcher matcher,
        SubtitleRecapOptions options,
        IReadOnlyList<double>? blackFrameTimes = null)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(options);

        if (cues is null || cues.Count == 0)
        {
            return null;
        }

        var ordered = cues.OrderBy(static c => c.Start).ThenBy(static c => c.End).ToList();

        // 1) Anchor: the first cue inside the opening window whose text is a recap opening.
        var anchorIndex = -1;
        var matchedPhrase = string.Empty;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Start > options.MaxWindowSeconds)
            {
                break;
            }

            if (matcher.TryMatch(ordered[i].Text, out matchedPhrase))
            {
                anchorIndex = i;
                break;
            }
        }

        if (anchorIndex < 0)
        {
            return null;
        }

        var anchor = ordered[anchorIndex];
        var start = anchor.Start;
        var end = anchor.End;
        var cueCount = 1;

        // 2) Grow the dense cluster forward while consecutive cues stay within the gap limit.
        for (var i = anchorIndex + 1; i < ordered.Count; i++)
        {
            var cue = ordered[i];
            if (cue.Start - end > options.MaxClusterGapSeconds)
            {
                break;
            }

            if (cue.End > end)
            {
                end = cue.End;
            }

            cueCount++;
        }

        // 3) Snap the end forward to a black frame (the montage fade-out), if one is close enough.
        var snapped = false;
        if (options.BlackFrameSnapSeconds > 0 && blackFrameTimes is { Count: > 0 })
        {
            var snapEnd = end;
            var bestDelta = double.MaxValue;
            foreach (var time in blackFrameTimes)
            {
                var delta = time - end;

                // Accept a small backward tolerance (cluster end may overshoot the fade by <1s)
                // and a forward window up to BlackFrameSnapSeconds.
                if (delta >= -1.0 && delta <= options.BlackFrameSnapSeconds && Math.Abs(delta) < bestDelta)
                {
                    bestDelta = Math.Abs(delta);
                    snapEnd = time;
                }
            }

            if (snapEnd != end)
            {
                end = snapEnd;
                snapped = true;
            }
        }

        // 4) Optional start-to-zero snap (opt-in, unlike the current forced behavior).
        if (options.SnapStartToZero && start <= options.StartSnapSeconds)
        {
            start = 0;
        }

        // 5) Clamp the duration and validate.
        if (end - start > options.MaxDurationSeconds)
        {
            end = start + options.MaxDurationSeconds;
        }

        if (end - start < options.MinDurationSeconds)
        {
            return null;
        }

        return new SubtitleRecapResult(start, end, matchedPhrase, anchor.Text, cueCount, snapped);
    }
}
