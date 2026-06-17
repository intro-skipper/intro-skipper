// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

internal readonly record struct QueueVerificationResult(IReadOnlyList<QueuedEpisode> Episodes, int SkippedCount);
