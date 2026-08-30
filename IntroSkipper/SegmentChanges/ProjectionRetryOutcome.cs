// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Result of a manual retry request.</summary>
/// <param name="Scope">Requested scope.</param>
/// <param name="RetriedCount">Number of items whose pending work the retry applied.</param>
/// <param name="Status">Aggregate status after retrying.</param>
public sealed record ProjectionRetryOutcome(ProjectionScope Scope, int RetriedCount, ProjectionStatus Status);
