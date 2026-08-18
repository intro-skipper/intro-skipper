// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.Db;

/// <summary>One row in a plan's immutable full own-row image.</summary>
internal sealed class DbProjectionPlanSegment
{
    public Guid ChangeId { get; set; }

    public Guid ItemId { get; set; }

    public int Position { get; set; }

    public Guid SegmentId { get; set; }

    public MediaSegmentType Type { get; set; }

    public long StartTicks { get; set; }

    public long EndTicks { get; set; }

    public SegmentSource Source { get; set; }
}
