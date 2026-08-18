// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.SegmentChanges;
using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.Db;

/// <summary>One ordered exact external operation in an immutable plan.</summary>
internal sealed class DbProjectionExternalOperation
{
    public Guid ChangeId { get; set; }

    public Guid ItemId { get; set; }

    public long Sequence { get; set; }

    public int Position { get; set; }

    public Guid ExternalSegmentId { get; set; }

    public MediaSegmentType ExpectedType { get; set; }

    public ProjectionExternalOperationKind Kind { get; set; }
}
