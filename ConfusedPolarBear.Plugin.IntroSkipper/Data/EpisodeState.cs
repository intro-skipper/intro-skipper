using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Data;

/// <summary>
/// Represents the state of an episode regarding analysis and blacklist status.
/// </summary>
public class EpisodeState
{
    private readonly Dictionary<MediaSegmentType, (bool Analyzed, bool Blacklisted)> _states = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeState"/> class.
    /// </summary>
    public EpisodeState() =>
        Array.ForEach(Enum.GetValues<MediaSegmentType>(), mode => _states[mode] = default);

    /// <summary>
    /// Checks if the specified analysis mode has been analyzed.
    /// </summary>
    /// <param name="mode">The analysis mode to check.</param>
    /// <returns>True if the mode has been analyzed, false otherwise.</returns>
    public bool IsAnalyzed(MediaSegmentType mode) => _states[mode].Analyzed;

    /// <summary>
    /// Checks if the specified analysis mode has been blacklisted.
    /// </summary>
    /// <param name="mode">The analysis mode to check.</param>
    /// <returns>True if the mode has been blacklisted, false otherwise.</returns>
    public bool IsBlacklisted(MediaSegmentType mode) => _states[mode].Blacklisted;

    /// <summary>
    /// Sets the analyzed state for the specified analysis mode.
    /// </summary>
    /// <param name="mode">The analysis mode to set.</param>
    /// <param name="value">The analyzed state to set.</param>
    public void SetAnalyzed(MediaSegmentType mode, bool value) =>
        _states[mode] = (value, _states[mode].Blacklisted);

    /// <summary>
    /// Sets the blacklisted state for the specified analysis mode.
    /// </summary>
    /// <param name="mode">The analysis mode to set.</param>
    /// <param name="value">The blacklisted state to set.</param>
    public void SetBlacklisted(MediaSegmentType mode, bool value) =>
        _states[mode] = (_states[mode].Analyzed, value);

    /// <summary>
    /// Resets all states to their default values.
    /// </summary>
    public void ResetStates() =>
        Array.ForEach(Enum.GetValues<MediaSegmentType>(), mode => _states[mode] = default);

    /// <summary>
    /// Gets all modes that have been analyzed.
    /// </summary>
    /// <returns>An IEnumerable of analyzed MediaSegmentTypes.</returns>
    public IEnumerable<MediaSegmentType> GetAnalyzedModes() =>
        _states.Where(kvp => kvp.Value.Analyzed).Select(kvp => kvp.Key);

    /// <summary>
    /// Gets all modes that have been blacklisted.
    /// </summary>
    /// <returns>An IEnumerable of blacklisted MediaSegmentTypes.</returns>
    public IEnumerable<MediaSegmentType> GetBlacklistedModes() =>
        _states.Where(kvp => kvp.Value.Blacklisted).Select(kvp => kvp.Key);
}
