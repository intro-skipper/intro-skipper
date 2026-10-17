// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Idempotency record for one remote analysis submission.
/// </summary>
internal sealed class DbRemoteImportReceipt
{
    public Guid JobId { get; set; }

    public Guid AnalysisId { get; set; }

    public Guid ItemId { get; set; }

    public int Mode { get; set; }

    public string PayloadHash { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public string? OriginalCanonicalJson { get; set; }

    public DateTime CreatedUtc { get; set; }
}
