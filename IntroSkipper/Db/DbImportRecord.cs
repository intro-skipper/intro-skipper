// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Marker row recording one attempt to import the legacy <c>introskipper.db</c> into
/// the current database. The presence of any row means the import question has been
/// answered for this database file and initialization must not import again — even
/// when the legacy file (re)appears later. Re-importing requires deleting the
/// current database file and restarting the server.
/// </summary>
public class DbImportRecord
{
    /// <summary>
    /// Gets or sets the primary key.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the UTC time the import completed.
    /// </summary>
    public DateTime ImportedAt { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a legacy database file existed.
    /// </summary>
    public bool SourceFileFound { get; set; }

    /// <summary>
    /// Gets or sets the number of segment rows imported.
    /// </summary>
    public int SegmentsImported { get; set; }

    /// <summary>
    /// Gets or sets the number of legacy segment rows skipped (invalid or duplicate).
    /// </summary>
    public int SegmentsSkipped { get; set; }

    /// <summary>
    /// Gets or sets the number of season-state rows imported.
    /// </summary>
    public int SeasonStatesImported { get; set; }

    /// <summary>
    /// Gets or sets free-form diagnostics (the detected legacy shape).
    /// </summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC time the one-time projection-backlog reconciliation
    /// (<see cref="IntroSkipperDatabase.ReconcileProjectionBacklogAsync"/>) completed, or
    /// <see langword="null"/> if it has not run yet. This row already exists exactly once
    /// per database (created the first time <see cref="IntroSkipperDatabase.InitializeCoreAsync"/>
    /// runs, whether or not a legacy file was found), so it doubles as the marker for this
    /// unrelated one-time concern rather than adding a second single-row table for it. An
    /// install whose <see cref="ImportedAt"/> predates this field's existence still reads it
    /// as <see langword="null"/>, so the reconciliation still runs once for installs that
    /// migrated before this fix shipped — the property this marker exists to guarantee.
    /// </summary>
    public DateTime? ProjectionBacklogReconciledAt { get; set; }
}
