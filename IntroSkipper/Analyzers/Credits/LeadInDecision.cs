// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// What the lead-in probe concluded about a nominated boundary. Every outcome leaves the scene's
/// start between its candidate start and the first keyframe at its black level: <see cref="Keep"/>
/// leaves it at the candidate start, <see cref="TrimAt"/> moves it to the frame where the level
/// changes, and <see cref="Inconclusive"/> leaves it at that keyframe, the policy's start.
/// </summary>
internal abstract record LeadInDecision
{
    private LeadInDecision()
    {
    }

    /// <summary>
    /// The prefix is credits: the scene keeps its candidate start.
    /// </summary>
    internal sealed record Keep : LeadInDecision;

    /// <summary>
    /// The prefix is not credits: the scene starts at the frame where the background reached the level.
    /// </summary>
    /// <param name="Time">Media time of that frame in seconds.</param>
    internal sealed record TrimAt(double Time) : LeadInDecision;

    /// <summary>
    /// The probe could not observe enough to decide.
    /// </summary>
    /// <param name="Reason">What was missing, for the log.</param>
    internal sealed record Inconclusive(string Reason) : LeadInDecision;
}
