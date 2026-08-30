// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Thrown when a database rebuild aborts because the existing database could not be read
/// for backup and the caller did not request a clean rebuild. Retrying with
/// <c>forceCleanOnBackupFailure</c> discards the unreadable data and rebuilds empty.
/// Derives from <see cref="InvalidOperationException"/> so callers catching the general
/// type keep working.
/// </summary>
public sealed class DatabaseRebuildBackupException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseRebuildBackupException"/> class.
    /// </summary>
    public DatabaseRebuildBackupException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseRebuildBackupException"/> class.
    /// </summary>
    /// <param name="message">Error message.</param>
    public DatabaseRebuildBackupException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseRebuildBackupException"/> class.
    /// </summary>
    /// <param name="message">Error message.</param>
    /// <param name="innerException">The backup read failure.</param>
    public DatabaseRebuildBackupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
