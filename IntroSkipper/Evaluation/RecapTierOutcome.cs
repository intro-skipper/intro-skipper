// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Evaluation;

/// <summary>
/// The outcome of running the tiered recap pipeline over one episode: which tier won (if any) and
/// the resolved interval.
/// </summary>
/// <param name="Tier">The tier that produced the interval, or <see cref="RecapTier.None"/>.</param>
/// <param name="Interval">The resolved recap interval (empty when no tier fired).</param>
internal readonly record struct RecapTierOutcome(RecapTier Tier, RecapInterval Interval)
{
    /// <summary>
    /// Gets an outcome representing "no tier fired".
    /// </summary>
    public static RecapTierOutcome None => new(RecapTier.None, RecapInterval.Empty);

    /// <summary>
    /// Gets a value indicating whether a tier produced a valid interval.
    /// </summary>
    public bool Fired => Tier != RecapTier.None && Interval.HasValue;

    /// <summary>
    /// Gets the lower-case signal name for the winning tier, suitable for <see cref="RecapDetection.Signal"/>.
    /// </summary>
    public string Signal => Tier switch
    {
        RecapTier.Chapter => "chapter",
        RecapTier.Subtitle => "subtitle",
        RecapTier.Sting => "sting",
        _ => "none",
    };
}
