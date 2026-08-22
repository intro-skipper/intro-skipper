// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// A titled block of the support bundle holding either <see cref="Entries"/> (a list of facts) or
/// <see cref="Text"/> (a preformatted block such as raw FFmpeg output).
/// </summary>
/// <param name="Title">Section title.</param>
/// <param name="Collapsed">Whether the body is noise that should stay folded until expanded. The Markdown
/// rendering wraps such sections in a details element so GitHub collapses them too.</param>
public sealed record SupportBundleSection(string Title, bool Collapsed = false)
{
    /// <summary>
    /// Gets the facts in this section, or <see langword="null"/> for a text section.
    /// </summary>
    public IReadOnlyList<SupportBundleEntry>? Entries { get; init; }

    /// <summary>
    /// Gets the preformatted text of this section, or <see langword="null"/> for an entries section.
    /// </summary>
    public string? Text { get; init; }
}
