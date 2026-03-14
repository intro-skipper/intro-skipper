// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace IntroSkipper.Data;

/// <summary>
/// Represents the current scan status returned by the scan status endpoint.
/// </summary>
/// <param name="IsRunning">Whether a scan is currently in progress.</param>
public record ScanStatusResponse([property: JsonPropertyName("isRunning")] bool IsRunning);
