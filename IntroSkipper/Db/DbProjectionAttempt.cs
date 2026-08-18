// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.SegmentChanges;

namespace IntroSkipper.Db;

/// <summary>Mutable retry metadata for one pending item plan.</summary>
internal sealed class DbProjectionAttempt
{
    public Guid ChangeId { get; set; }

    public Guid ItemId { get; set; }

    public ProjectionState Status { get; set; }

    public int AttemptCount { get; set; }

    public DateTime? LastAttemptAt { get; set; }

    public DateTime? NextAttemptAt { get; set; }

    public string? Failure { get; set; }
}
