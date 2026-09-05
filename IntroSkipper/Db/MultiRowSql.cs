// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.CompilerServices;

namespace IntroSkipper.Db;

/// <summary>
/// Composes parameterized multi-row <c>INSERT ... VALUES</c> statements for the bulk
/// per-item upserts (projection markers, analysis records), so a library-wide write
/// costs a handful of statements instead of one per item while holding SQLite's
/// write lock. Every value is bound as a parameter; nothing is spliced into SQL text.
/// </summary>
internal static class MultiRowSql
{
    /// <summary>
    /// Ids per statement. SQLite binds at most 32766 parameters per statement; rows
    /// of a few columns stay far below that at this size.
    /// </summary>
    public const int ChunkSize = 500;

    /// <summary>
    /// Yields one statement per <see cref="ChunkSize"/> ids.
    /// </summary>
    /// <param name="ids">Ids to write, one row each. Callers dedupe first.</param>
    /// <param name="row">Builds one parenthesized row from the id's placeholder (<c>{n}</c>).
    /// Placeholders <c>{0}</c> to <c>{shared.Length - 1}</c> bind the <paramref name="shared"/> values.</param>
    /// <param name="statement">Wraps the comma-joined rows into the full <c>INSERT ... VALUES rows ON CONFLICT ...</c> text.</param>
    /// <param name="shared">Values every row binds, such as a mode or a config hash.</param>
    /// <returns>Statements ready for <c>ExecuteSqlAsync</c>.</returns>
    public static IEnumerable<FormattableString> Statements(IEnumerable<Guid> ids, Func<string, string> row, Func<string, string> statement, params object[] shared)
    {
        foreach (var chunk in ids.Chunk(ChunkSize))
        {
            var rows = string.Join(", ", chunk.Select((_, index) => row($"{{{shared.Length + index}}}")));
            yield return FormattableStringFactory.Create(statement(rows), [.. shared, .. chunk.Cast<object>()]);
        }
    }
}
