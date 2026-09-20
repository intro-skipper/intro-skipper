// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Process-wide warnings shown in the support bundle. Fingerprint failures are kept as a bounded list of
/// descriptions naming the file, cleared when a full library scan starts, so the bundle reports the
/// failures since the last full scan. Safe to call from parallel analysis.
/// </summary>
internal static class WarningManager
{
    /// <summary>
    /// How many fingerprint failure descriptions are kept; later ones are counted only.
    /// </summary>
    public const int MaxFingerprintFailures = 50;

    private static readonly Lock _lock = new();
    private static readonly List<string> _fingerprintFailures = [];
    private static PluginWarning _warnings;
    private static int _droppedFingerprintFailures;

    /// <summary>
    /// Set warning.
    /// </summary>
    /// <param name="warning">Warning.</param>
    public static void SetFlag(PluginWarning warning)
    {
        lock (_lock)
        {
            _warnings |= warning;
        }
    }

    /// <summary>
    /// Get warnings.
    /// </summary>
    /// <returns>Warnings.</returns>
    public static string GetWarnings()
    {
        lock (_lock)
        {
            return _warnings.ToString();
        }
    }

    /// <summary>
    /// Records a file that could not be fingerprinted and raises
    /// <see cref="PluginWarning.InvalidChromaprintFingerprint"/>.
    /// </summary>
    /// <param name="description">What failed, naming the file, e.g. the <see cref="FingerprintException"/> message.</param>
    public static void RecordFingerprintFailure(string description)
    {
        lock (_lock)
        {
            _warnings |= PluginWarning.InvalidChromaprintFingerprint;
            if (_fingerprintFailures.Count < MaxFingerprintFailures)
            {
                _fingerprintFailures.Add(description);
            }
            else
            {
                _droppedFingerprintFailures++;
            }
        }
    }

    /// <summary>
    /// Forgets recorded fingerprint failures and clears
    /// <see cref="PluginWarning.InvalidChromaprintFingerprint"/>.
    /// </summary>
    public static void ResetFingerprintFailures()
    {
        lock (_lock)
        {
            _warnings &= ~PluginWarning.InvalidChromaprintFingerprint;
            _fingerprintFailures.Clear();
            _droppedFingerprintFailures = 0;
        }
    }

    /// <summary>
    /// Gets the recorded fingerprint failure descriptions and how many more were dropped past
    /// <see cref="MaxFingerprintFailures"/>.
    /// </summary>
    /// <returns>A snapshot of the kept descriptions and the dropped count.</returns>
    public static (IReadOnlyList<string> Failures, int Dropped) GetFingerprintFailures()
    {
        lock (_lock)
        {
            return ([.. _fingerprintFailures], _droppedFingerprintFailures);
        }
    }
}
