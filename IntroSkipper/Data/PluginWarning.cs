// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2025 AbandonedCart
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;

namespace IntroSkipper.Data;

/// <summary>
/// Support bundle warning.
/// </summary>
[Flags]
public enum PluginWarning
{
    /// <summary>
    /// No warnings have been added.
    /// </summary>
    None = 0,

    /// <summary>
    /// At least one media file on the server was unable to be fingerprinted by Chromaprint.
    /// </summary>
    InvalidChromaprintFingerprint = 2,

    /// <summary>
    /// The version of ffmpeg installed on the system is not compatible with the plugin.
    /// </summary>
    IncompatibleFFmpegBuild = 4,
}
