// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>Immutable durable projection plan header.</summary>
internal sealed class DbProjectionPlan
{
    public Guid ChangeId { get; set; }

    public Guid ItemId { get; set; }

    public long Sequence { get; set; }

    public DateTime CreatedAt { get; set; }
}
