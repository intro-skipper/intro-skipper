// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.CompilerServices;

namespace IntroSkipper.Db;

/// <summary>
/// Composes parameterized multi-row <c>VALUES</c> statements for the bulk per-item
/// writes (projection markers, analysis records), so a library-wide write costs a
/// handful of statements instead of one per item while holding SQLite's write lock.
/// Every value is bound as a parameter; nothing is spliced into SQL text.
/// </summary>
internal static class MultiRowSql
{
    /// <summary>
    /// Rows per statement. SQLite binds at most 32766 parameters per statement; rows
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
        => Statements(ids, id => [id], placeholders => row(placeholders[0]), statement, shared);

    /// <summary>
    /// Yields one statement per <see cref="ChunkSize"/> rows, binding several values per row.
    /// </summary>
    /// <typeparam name="TRow">Source row type.</typeparam>
    /// <param name="rows">Rows to write. Callers dedupe first.</param>
    /// <param name="values">The values one row binds, in column order. Every row binds the same count.</param>
    /// <param name="row">Builds one parenthesized row from its values' placeholders (<c>{n}</c>), in the
    /// order <paramref name="values"/> returned them. Placeholders <c>{0}</c> to
    /// <c>{shared.Length - 1}</c> bind the <paramref name="shared"/> values.</param>
    /// <param name="statement">Wraps the comma-joined rows into the full statement text.</param>
    /// <param name="shared">Values every row binds, such as a mode or a config hash.</param>
    /// <returns>Statements ready for <c>ExecuteSqlAsync</c>.</returns>
    public static IEnumerable<FormattableString> Statements<TRow>(IEnumerable<TRow> rows, Func<TRow, object?[]> values, Func<string[], string> row, Func<string, string> statement, params object[] shared)
    {
        foreach (var chunk in rows.Chunk(ChunkSize))
        {
            var arguments = new List<object?>(shared);
            var rowTexts = new List<string>(chunk.Length);
            foreach (var source in chunk)
            {
                var rowValues = values(source);
                var placeholders = new string[rowValues.Length];
                for (var i = 0; i < rowValues.Length; i++)
                {
                    placeholders[i] = $"{{{arguments.Count}}}";
                    arguments.Add(rowValues[i]);
                }

                rowTexts.Add(row(placeholders));
            }

            yield return FormattableStringFactory.Create(statement(string.Join(", ", rowTexts)), [.. arguments]);
        }
    }
}
