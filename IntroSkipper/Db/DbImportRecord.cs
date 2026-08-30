// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
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
}
