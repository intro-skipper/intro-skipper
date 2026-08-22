// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Text;

namespace IntroSkipper.Data;

/// <summary>
/// Diagnostics snapshot shown on the dashboard's Information tab and pasted into bug reports.
/// </summary>
/// <param name="Sections">Sections in display order.</param>
public sealed record SupportBundle(IReadOnlyList<SupportBundleSection> Sections)
{
    /// <summary>
    /// Gets the bundle rendered as GitHub-flavoured Markdown: entry sections become bullet lists with the values in
    /// code spans so they render verbatim, text sections become fenced code blocks, and collapsed sections are
    /// wrapped in a details element.
    /// </summary>
    public string Markdown { get; } = Render(Sections);

    private static string Render(IReadOnlyList<SupportBundleSection> sections)
    {
        var markdown = new StringBuilder();

        foreach (var section in sections)
        {
            if (markdown.Length > 0)
            {
                markdown.Append('\n');
            }

            if (section.Collapsed)
            {
                markdown.Append("<details>\n<summary>").Append(section.Title).Append("</summary>\n\n");
                AppendBody(markdown, section);
                markdown.Append("\n</details>\n");
            }
            else
            {
                markdown.Append("**").Append(section.Title).Append("**\n\n");
                AppendBody(markdown, section);
            }
        }

        return markdown.ToString();
    }

    private static void AppendBody(StringBuilder markdown, SupportBundleSection section)
    {
        if (section.Text is { } text)
        {
            // A fence longer than any backtick run in the text keeps the block intact.
            var fence = new string('`', Math.Max(3, LongestBacktickRun(text) + 1));
            markdown.Append(fence).Append('\n').Append(text);
            if (!text.EndsWith('\n'))
            {
                markdown.Append('\n');
            }

            markdown.Append(fence).Append('\n');
            return;
        }

        if (section.Entries is not { Count: > 0 } entries)
        {
            markdown.Append("None\n");
            return;
        }

        foreach (var (label, value) in entries)
        {
            markdown.Append("* ").Append(label).Append(": ").Append(CodeSpan(value)).Append('\n');
        }
    }

    // Wraps a value in a code span longer than any backtick run it contains, so regex patterns and paths survive
    // Markdown rendering untouched. CommonMark strips one space from each end of a span that starts and ends with
    // one, so values that begin or end with a backtick or a space get an extra padding space on both sides. An
    // all-space value is left alone because the stripping rule skips those.
    private static string CodeSpan(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var longestRun = LongestBacktickRun(value);
        var delimiter = new string('`', longestRun + 1);
        var needsPadding = longestRun > 0
            || ((value[0] == ' ' || value[^1] == ' ') && value.AsSpan().Trim(' ').Length > 0);
        return needsPadding ? delimiter + " " + value + " " + delimiter : delimiter + value + delimiter;
    }

    private static int LongestBacktickRun(string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }
}
