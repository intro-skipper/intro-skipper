// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Evaluation;

/// <summary>
/// The scored outcome for a single labeled episode: how the detection compared to the truth.
/// </summary>
internal sealed class RecapItemResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RecapItemResult"/> class.
    /// </summary>
    /// <param name="label">The ground-truth label.</param>
    /// <param name="detected">The detected interval (empty when nothing fired).</param>
    /// <param name="iouMatchThreshold">IoU threshold used to decide a correct match.</param>
    public RecapItemResult(RecapLabel label, RecapInterval detected, double iouMatchThreshold)
    {
        ArgumentNullException.ThrowIfNull(label);

        Label = label;
        Detected = detected;
        Fired = detected.HasValue;
        IoU = RecapMetrics.IntersectionOverUnion(detected, label.Truth);
        Classification = Classify(label.HasRecap, Fired, RecapMetrics.IsMatch(detected, label.Truth, iouMatchThreshold));

        // Boundary errors are only meaningful when there is both a true recap and a firing
        // detection. They are intentionally independent of the IoU match threshold so a
        // poorly localized hit still contributes its boundary error.
        if (label.HasRecap && Fired)
        {
            StartError = RecapMetrics.AbsoluteStartError(detected, label.Truth);
            EndError = RecapMetrics.AbsoluteEndError(detected, label.Truth);
        }
    }

    /// <summary>
    /// Gets the ground-truth label.
    /// </summary>
    public RecapLabel Label { get; }

    /// <summary>
    /// Gets the detected interval (empty when nothing fired).
    /// </summary>
    public RecapInterval Detected { get; }

    /// <summary>
    /// Gets a value indicating whether the detector produced a valid interval for this episode.
    /// </summary>
    public bool Fired { get; }

    /// <summary>
    /// Gets the IoU between the detected and truth intervals (0 when either is empty).
    /// </summary>
    public double IoU { get; }

    /// <summary>
    /// Gets the confusion-matrix classification for this episode.
    /// </summary>
    public RecapClassification Classification { get; }

    /// <summary>
    /// Gets the absolute start-boundary error, in seconds, or <see langword="null"/> when not applicable.
    /// </summary>
    public double? StartError { get; }

    /// <summary>
    /// Gets the absolute end-boundary error, in seconds, or <see langword="null"/> when not applicable.
    /// </summary>
    public double? EndError { get; }

    private static RecapClassification Classify(bool hasRecap, bool fired, bool matched)
    {
        if (hasRecap)
        {
            return fired && matched ? RecapClassification.TruePositive : RecapClassification.FalseNegative;
        }

        return fired ? RecapClassification.FalsePositive : RecapClassification.TrueNegative;
    }
}
