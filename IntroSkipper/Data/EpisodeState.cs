// Copyright (C) 2024 Intro-Skipper Contributors <intro-skipper.org>
// SPDX-License-Identifier: GNU General Public License v3.0 only.

using System;

namespace IntroSkipper.Data;

/// <summary>
/// Represents the state of an episode regarding analysis and blacklist status.
/// </summary>
public class EpisodeState
{
    private readonly bool[] _analyzedStates = new bool[2];

    private readonly bool[] _blacklistedStates = new bool[2];

    /// <summary>
    /// Checks if the specified analysis mode has been analyzed.
    /// </summary>
    /// <param name="mode">The analysis mode to check.</param>
    /// <returns>True if the mode has been analyzed, false otherwise.</returns>
    public bool IsAnalyzed(AnalysisMode mode) => _analyzedStates[(int)mode];

    /// <summary>
    /// Sets the analyzed state for the specified analysis mode.
    /// </summary>
    /// <param name="mode">The analysis mode to set.</param>
    /// <param name="value">The analyzed state to set.</param>
    public void SetAnalyzed(AnalysisMode mode, bool value) => _analyzedStates[(int)mode] = value;

    /// <summary>
    /// Checks if the specified analysis mode has been blacklisted.
    /// </summary>
    /// <param name="mode">The analysis mode to check.</param>
    /// <returns>True if the mode has been blacklisted, false otherwise.</returns>
    public bool IsBlacklisted(AnalysisMode mode) => _blacklistedStates[(int)mode];

    /// <summary>
    /// Sets the blacklisted state for the specified analysis mode.
    /// </summary>
    /// <param name="mode">The analysis mode to set.</param>
    /// <param name="value">The blacklisted state to set.</param>
    public void SetBlacklisted(AnalysisMode mode, bool value) => _blacklistedStates[(int)mode] = value;

    /// <summary>
    /// Resets the analyzed states.
    /// </summary>
    public void ResetStates()
    {
        Array.Clear(_analyzedStates);
        Array.Clear(_blacklistedStates);
    }
}
