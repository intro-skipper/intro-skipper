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
    /// Gets the fraction of sampled frames that meet the black-frame threshold.
    /// </summary>
    public double BlackFrameDensity => TotalFrameCount == 0 ? 0 : (double)BlackFrameCount / TotalFrameCount;

    /// <summary>
    /// Determines whether this measurement satisfies a caller-supplied density threshold.
    /// </summary>
    /// <param name="minimumDensity">The required black-frame density.</param>
    /// <returns><see langword="true" /> if the measured density is at least <paramref name="minimumDensity" />; otherwise, <see langword="false" />.</returns>
    public bool MeetsDensity(double minimumDensity) => TotalFrameCount > 0 && BlackFrameDensity >= minimumDensity;

    /// <summary>
    /// Determines whether the candidate is too sparse to trust without interval support.
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
    /// <param name="frames">The keyframe black-frame scan results.</param>
    /// <param name="scene">The candidate scene to measure.</param>
    /// <param name="minimum">The minimum black percentage that marks a frame as black.</param>
    /// <returns>The calculated scene metrics.</returns>
    public static CreditSceneMetrics Calculate(IReadOnlyList<BlackFrame> frames, CreditScene scene, int minimum)
    {
        var totalFrameCount = 0;
        var blackFrameCount = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.Time < scene.StartTime)
            {
                continue;
            }

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
}
