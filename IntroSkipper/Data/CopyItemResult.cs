// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Per-target outcome of a copy operation.
/// </summary>
/// <param name="ItemId">Target item id.</param>
/// <param name="Success">Whether the copy to this target succeeded.</param>
/// <param name="Error">Failure description when unsuccessful.</param>
public sealed record CopyItemResult(Guid ItemId, bool Success, string? Error);
