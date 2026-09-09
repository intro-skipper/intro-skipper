// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace IntroSkipper.Helper;

/// <summary>
/// Represents the payload for a patch request.
/// </summary>
public class PayloadRequest
{
    /// <summary>
    /// Gets or sets represents the contents of the patch request.
    /// </summary>
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}
