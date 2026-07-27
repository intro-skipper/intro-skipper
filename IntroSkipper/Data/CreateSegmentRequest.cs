// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace IntroSkipper.Data;

/// <summary>
/// Request body for creating a user segment via the plural segments API.
/// </summary>
/// <param name="Type">Analysis mode the segment belongs to (name or numeric value).</param>
/// <param name="Start">Start time in seconds.</param>
/// <param name="End">End time in seconds; must be after <paramref name="Start"/>.</param>
public sealed record CreateSegmentRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter<AnalysisMode>))] AnalysisMode Type,
    double Start,
    double End);
