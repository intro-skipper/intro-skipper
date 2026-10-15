// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

internal readonly record struct EmbeddedChapterTransitionState(
    bool AppliedEmbeddedChapters,
    bool? PendingEmbeddedChapters);
