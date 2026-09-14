// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace IntroSkipper.Data;

/// <summary>
/// The scan status endpoint's answer for one season.
/// </summary>
/// <param name="IsRunning">Whether any scan is running: a pass in flight, or a manual scan or library pass pending.</param>
/// <param name="IsQueued">Whether the season's own manual scan is pending or running.</param>
/// <param name="Failed">Whether the season's most recent completed manual scan failed.</param>
public sealed record ScanStatusResponse(
    [property: JsonPropertyName("isRunning")] bool IsRunning,
    [property: JsonPropertyName("isQueued")] bool IsQueued,
    [property: JsonPropertyName("failed")] bool Failed);
