// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Where the lead-in probe places the start of a black scene after its dark grey lead-in. Both
/// outcomes leave the start between the last keyframe lighter than the scene's black level and the
/// first keyframe at it: <see cref="TrimAt"/> moves it to the frame where the level changes, and
/// <see cref="Inconclusive"/> leaves it at that keyframe, the policy's start.
/// </summary>
internal abstract record LeadInDecision
{
    private LeadInDecision()
    {
    }

    /// <summary>
    /// The scene starts at the frame where the background reached the level.
    /// </summary>
    /// <param name="Time">Media time of that frame in seconds.</param>
    internal sealed record TrimAt(double Time) : LeadInDecision;

    /// <summary>
    /// The probe could not observe a level change; the scene starts at the first keyframe at the level.
    /// </summary>
    /// <param name="Reason">What was missing, for the log.</param>
    internal sealed record Inconclusive(string Reason) : LeadInDecision;
}
