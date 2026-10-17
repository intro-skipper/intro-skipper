// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Current accepted remote analysis provenance and monotonically increasing revision.
/// </summary>
internal sealed class DbRemoteAnalysisMetadata
{
    public Guid ItemId { get; set; }

    public int Mode { get; set; }

    public long Revision { get; set; }

    public string? CanonicalJson { get; set; }

    public string? DetectorId { get; set; }

    public string? ProfileHash { get; set; }

    public string? UpstreamCommit { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
