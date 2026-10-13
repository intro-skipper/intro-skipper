// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Durable state for the chapter-source transition reanalysis. The row is a singleton
/// so a server restart can distinguish the source used by the last completed pass from
/// a transition that was interrupted before it completed.
/// </summary>
public sealed class DbEmbeddedChapterTransition
{
    /// <summary>
    /// Gets or sets the singleton key.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the last completed transition used
    /// chapters embedded in the media file.
    /// </summary>
    public bool AppliedEmbeddedChapters { get; set; }

    /// <summary>
    /// Gets or sets the source requested by an in-progress transition, or <see langword="null"/>
    /// when no transition is pending.
    /// </summary>
    public bool? PendingEmbeddedChapters { get; set; }
}
