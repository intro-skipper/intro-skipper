// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Represents density and sparsity measurements for a credit-scene candidate.
/// </summary>
/// <param name="TotalFrameCount">The number of sampled frames inside the candidate.</param>
/// <param name="BlackFrameCount">The number of sampled frames that meet the black-frame threshold.</param>
internal readonly record struct CreditSceneMetrics(int TotalFrameCount, int BlackFrameCount)
{
    /// <summary>
    /// Determines whether the fraction of sampled frames that meet the black-frame threshold satisfies a caller-supplied density.
    /// </summary>
    /// <param name="minimumDensity">The required black-frame density.</param>
    /// <returns><see langword="true" /> if the measured density is at least <paramref name="minimumDensity" />; otherwise, <see langword="false" />.</returns>
    public bool MeetsDensity(double minimumDensity) => TotalFrameCount > 0 && (double)BlackFrameCount / TotalFrameCount >= minimumDensity;

    /// <summary>
    /// Determines whether the scene's black-frame samples are temporally sparse relative to the
    /// minimum duration. Sparse evidence triggers an opportunistic blackdetect interval probe to
    /// refine or better anchor the boundaries; it does not, on its own, invalidate a scene that has
    /// already cleared the density and duration gates.
    /// </summary>
    /// <param name="scene">The candidate scene measured by this instance.</param>
    /// <param name="minimumDuration">The minimum credit duration.</param>
    /// <returns><see langword="true" /> if black-frame evidence is sparse for the configured duration; otherwise, <see langword="false" />.</returns>
    public bool IsSparse(CreditScene scene, int minimumDuration)
    {
        if (BlackFrameCount <= 1)
        {
            return true;
        }

        var averageBlackFrameGap = (scene.EndTime - scene.StartTime) / (BlackFrameCount - 1);
        return averageBlackFrameGap > CreditDetectionPolicy.MaximumSparseAverageBlackFrameGap(minimumDuration);
    }
}

/// <summary>
/// Calculates credit-scene metrics from keyframe black-frame scan results.
/// </summary>
internal static class CreditSceneMetricsCalculator
{
    /// <summary>
    /// Calculates density metrics for a candidate scene.
    /// </summary>
    /// <param name="frames">The keyframe black-frame scan results, in decode order.</param>
    /// <param name="scene">The candidate scene to measure.</param>
    /// <param name="minimum">The minimum black percentage that marks a frame as black.</param>
    /// <returns>The calculated scene metrics.</returns>
    public static CreditSceneMetrics Calculate(IReadOnlyList<BlackFrame> frames, CreditScene scene, int minimum)
    {
        var totalFrameCount = 0;
        var blackFrameCount = 0;
        for (var i = FirstIndexAtOrAfterTime(frames, scene.StartTime); i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.Time > scene.EndTime)
            {
                break;
            }

            totalFrameCount++;
            if (frame.Percentage >= minimum)
            {
                blackFrameCount++;
            }
        }

        return new CreditSceneMetrics(totalFrameCount, blackFrameCount);
    }

    /// <summary>
    /// Finds the first index whose frame time is at or after <paramref name="time"/>.
    /// </summary>
    /// <param name="frames">Frames in decode order, so times are non-decreasing.</param>
    /// <param name="time">Time to search for.</param>
    /// <returns>The index, or <c>frames.Count</c> when every frame is earlier.</returns>
    public static int FirstIndexAtOrAfterTime(IReadOnlyList<BlackFrame> frames, double time)
    {
        var low = 0;
        var high = frames.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (frames[mid].Time < time)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    /// <summary>
    /// Finds the first index whose frame number is at or after <paramref name="frameNumber"/>.
    /// </summary>
    /// <param name="frames">Frames in decode order, so frame numbers are non-decreasing.</param>
    /// <param name="frameNumber">Frame number to search for.</param>
    /// <returns>The index, or <c>frames.Count</c> when every frame is earlier.</returns>
    public static int FirstIndexAtOrAfterFrame(IReadOnlyList<BlackFrame> frames, int frameNumber)
    {
        var low = 0;
        var high = frames.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (frames[mid].Frame < frameNumber)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
