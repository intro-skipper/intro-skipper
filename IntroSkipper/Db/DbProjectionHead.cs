// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.SegmentChanges;

namespace IntroSkipper.Db;

/// <summary>Compacted projection progress for one item.</summary>
internal sealed class DbProjectionHead
{
    public Guid ItemId { get; set; }

    public long LastAcceptedSequence { get; set; }

    public long LastAppliedSequence { get; set; }

    public ProjectionState Status { get; set; }
}
