// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace IntroSkipper.Subtitles;

/// <summary>
/// Matches recap-opening phrases (e.g. "Previously on…") against subtitle cue text.
/// Matching is "anchored": structured leading noise (speaker labels, bracketed sound cues, dashes,
/// markup) is stripped first, then the cue must <em>begin</em> with a recap phrase on a normalized
/// form (lower-cased, diacritics removed, punctuation collapsed). This rejects an incidental
/// "…previously on…" buried inside a line of dialogue while still catching real openers preceded by
/// a short label such as "[NARRATOR] Previously on…".
/// </summary>
public sealed partial class RecapPhraseMatcher
{
    /// <summary>
    /// Default maximum residual character offset at which an anchored phrase may begin after leading
    /// noise has been stripped. Kept small: real recap openers begin the cue, so the structured strip
    /// (not a wide tolerance) is what absorbs labels.
    /// </summary>
    public const int DefaultAnchorTolerance = 2;

    private readonly string[] _normalizedPhrases;
    private readonly int _anchorTolerance;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecapPhraseMatcher"/> class.
    /// </summary>
    /// <param name="phrases">Recap-opening phrases to match (raw, in any casing/diacritics; each is normalized once here).</param>
    /// <param name="anchorTolerance">Maximum residual character offset at which a matched phrase may begin within the normalized, noise-stripped cue.</param>
    public RecapPhraseMatcher(IEnumerable<string> phrases, int anchorTolerance = DefaultAnchorTolerance)
    {
        ArgumentNullException.ThrowIfNull(phrases);

        _normalizedPhrases = phrases
            .Select(Normalize)
            .Where(static p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _anchorTolerance = Math.Max(0, anchorTolerance);
    }

    /// <summary>
    /// Gets the curated default list of recap-opening phrases across several major languages.
    /// This is the seed for the configurable plugin setting; it is deliberately biased toward
    /// precision (multi-word forms) over recall.
    /// </summary>
    public static IReadOnlyList<string> DefaultPhrases { get; } =
    [
        // English
        "previously on",
        "last time on",
        "last week on",
        "last season on",
        "earlier this season",
        // Spanish
        "anteriormente en",
        "en episodios anteriores",
        // Portuguese
        "anteriormente em",
        // French ("précédemment" -> "precedemment" after diacritics removal)
        "precedemment dans",
        "precedemment sur",
        // German
        "was bisher geschah",
        "was bisher passierte",
        "bisher bei",
        // Italian
        "negli episodi precedenti",
        "nelle puntate precedenti",
        // Dutch
        "wat voorafging",
        // Japanese (前回 / これまでの…)
        "前回",
        "これまでの",
        // Korean
        "지난 이야기",
    ];

    /// <summary>
    /// Gets a matcher built from <see cref="DefaultPhrases"/>.
    /// </summary>
    public static RecapPhraseMatcher Default { get; } = new(DefaultPhrases);

    /// <summary>
    /// Gets the number of normalized phrases this matcher will test against.
    /// </summary>
    public int PhraseCount => _normalizedPhrases.Length;

    /// <summary>
    /// Determines whether the supplied cue text begins (after leading-noise stripping) with a recap-opening phrase.
    /// </summary>
    /// <param name="cueText">Raw cue text.</param>
    /// <returns><see langword="true"/> when a recap-opening phrase is found at/near the cue start.</returns>
    public bool IsRecapOpening(string? cueText) => TryMatch(cueText, out _);

    /// <summary>
    /// Attempts to match a recap-opening phrase against the supplied cue text.
    /// </summary>
    /// <param name="cueText">Raw cue text.</param>
    /// <param name="matchedPhrase">The normalized phrase that matched, when successful.</param>
    /// <returns><see langword="true"/> when a recap-opening phrase is found at/near the cue start.</returns>
    public bool TryMatch(string? cueText, out string matchedPhrase)
    {
        matchedPhrase = string.Empty;
        if (string.IsNullOrWhiteSpace(cueText))
        {
            return false;
        }

        var normalized = Normalize(StripLeadingNoise(cueText));
        if (normalized.Length == 0)
        {
            return false;
        }

        foreach (var phrase in _normalizedPhrases)
        {
            var index = normalized.IndexOf(phrase, StringComparison.Ordinal);
            if (index >= 0 && index <= _anchorTolerance)
            {
                matchedPhrase = phrase;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Normalizes subtitle text for robust, locale-insensitive matching: strips HTML/VTT/ASS markup,
    /// removes diacritics, lower-cases, maps punctuation to spaces, and collapses whitespace.
    /// </summary>
    /// <param name="text">Raw text.</param>
    /// <returns>The normalized text.</returns>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Remove HTML/VTT tags (<i>, <c.classname>, <font …>) and ASS override blocks ({\an8}).
        var stripped = MarkupRegex().Replace(text, " ");

        // Decompose so diacritics become combining marks we can drop (Précédemment -> precedemment).
        var decomposed = stripped.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
            {
                builder.Append(ch);
            }
            else
            {
                // Punctuation/symbols become separators so "previously," matches "previously".
                builder.Append(' ');
            }
        }

        var collapsed = WhitespaceRegex().Replace(builder.ToString(), " ").Trim();
        return collapsed.ToLowerInvariant();
    }

    /// <summary>
    /// Strips structured leading noise from raw cue text: markup tags, leading bracketed/parenthetical
    /// sound cues, a single leading speaker label ("NAME:"), and dialogue dashes/quote glyphs. This is
    /// what makes "[NARRATOR] Previously on…" anchor while "I told you previously on Tuesday" does not.
    /// </summary>
    /// <param name="text">Raw cue text.</param>
    /// <returns>The text with leading noise removed.</returns>
    internal static string StripLeadingNoise(string text)
    {
        var current = text;
        var changed = true;
        while (changed)
        {
            changed = false;
            var trimmed = current.TrimStart(' ', '\t', '-', '\u2010', '\u2013', '\u2014', '*', '>', '"', '\'', '\u201C', '\u201D', '\u2018', '\u2019', '\u266A', '\u266B', '\u2669');
            if (!string.Equals(trimmed, current, StringComparison.Ordinal))
            {
                current = trimmed;
                changed = true;
            }

            var bracket = LeadingBracketRegex().Match(current);
            if (bracket.Success && bracket.Length > 0)
            {
                current = current[bracket.Length..];
                changed = true;
                continue;
            }

            var speaker = LeadingSpeakerRegex().Match(current);
            if (speaker.Success && speaker.Length > 0)
            {
                current = current[speaker.Length..];
                changed = true;
            }
        }

        return current;
    }

    [GeneratedRegex(@"<[^>]+>|\{[^}]*\}")]
    private static partial Regex MarkupRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // A leading bracketed/parenthetical/markup group: "[NARRATOR]", "(theme music)", "<i>".
    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]|\([^)]*\)|<[^>]*>)\s*")]
    private static partial Regex LeadingBracketRegex();

    // A single short leading speaker label ending in a colon, e.g. "JOHN:" or "Narrator:".
    // Excludes commas/semicolons so a real sentence is never mistaken for a label.
    [GeneratedRegex(@"^\s*[\p{L}0-9][\p{L}0-9 .'\-]{0,24}:\s+")]
    private static partial Regex LeadingSpeakerRegex();
}
