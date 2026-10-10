// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Frozen;
using System.Globalization;

namespace IntroSkipper.Helper;

/// <summary>
/// Holds the subtitle languages that subtitle detection reads, parsed from
/// <see cref="Configuration.PluginConfiguration.SubtitleLanguages"/>. The ISO 639-1, 639-2/B
/// and 639-2/T codes of a language (<c>de</c>, <c>ger</c>, <c>deu</c>) name the same language,
/// whichever form the setting or the file uses, and Norwegian Bokmål and Nynorsk (<c>nb</c>,
/// <c>nn</c>) count as Norwegian (<c>no</c>). An empty selection reads every language.
/// </summary>
internal sealed class SubtitleLanguageSelection
{
    // Codes that .NET maps to a different code than the one they should compare as: the
    // 639-2/B codes that differ from the 639-2/T code .NET reports, and Bokmål and Nynorsk,
    // which .NET gives codes of their own while files usually tag Norwegian tracks "nor".
    private static readonly KeyValuePair<string, string>[] _aliases =
    [
        new("alb", "sqi"), new("arm", "hye"), new("baq", "eus"), new("bur", "mya"), new("chi", "zho"),
        new("cze", "ces"), new("dut", "nld"), new("fre", "fra"), new("geo", "kat"), new("ger", "deu"),
        new("gre", "ell"), new("ice", "isl"), new("mac", "mkd"), new("mao", "mri"), new("may", "msa"),
        new("per", "fas"), new("rum", "ron"), new("slo", "slk"), new("tib", "bod"), new("wel", "cym"),
        new("nb", "nor"), new("nob", "nor"), new("nn", "nor"), new("nno", "nor"),
    ];

    // The aliases above, then every language code .NET knows (639-1, or 639-2 for a language
    // without a 639-1 code) and its 639-2/T code, each mapped to the 639-2/T code. The first
    // entry for a code wins, so an alias overrides .NET's own mapping.
    private static readonly FrozenDictionary<string, string> _terminologyCodes = _aliases
        .Concat(CultureInfo
            .GetCultures(CultureTypes.NeutralCultures)
            .Where(culture => culture.ThreeLetterISOLanguageName.Length == 3 && culture.Name.Length > 0)
            .SelectMany(culture => new[]
            {
                KeyValuePair.Create(culture.TwoLetterISOLanguageName, culture.ThreeLetterISOLanguageName),
                KeyValuePair.Create(culture.ThreeLetterISOLanguageName, culture.ThreeLetterISOLanguageName),
            }))
        .DistinctBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
        .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly FrozenSet<string> _languages;

    private SubtitleLanguageSelection(IEnumerable<string> languages)
    {
        _languages = languages.ToFrozenSet(StringComparer.Ordinal);
        Normalized = string.Join(',', _languages.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Gets the selected languages as sorted, comma-separated 639-2/T codes. A language .NET
    /// does not know keeps its code, lower-cased.
    /// </summary>
    /// <value>The normalized selection, or an empty string when every language is read.</value>
    public string Normalized { get; }

    /// <summary>
    /// Parses the comma-separated language codes of the setting. A code may carry a region
    /// (<c>pt-BR</c>); only its language part is compared.
    /// </summary>
    /// <param name="setting">The configured codes, or <see langword="null"/>.</param>
    /// <returns>The selection; empty when <paramref name="setting"/> names no code.</returns>
    public static SubtitleLanguageSelection Parse(string? setting)
        => new((setting ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Canonical));

    /// <summary>
    /// Gets a value indicating whether an embedded stream with this language tag is read.
    /// </summary>
    /// <param name="tag">The stream's language tag, or <see langword="null"/> when it has none.</param>
    /// <returns>
    /// <see langword="true"/> when the selection is empty, the stream is untagged or tagged
    /// <c>und</c>, or the tag names a selected language; otherwise, <see langword="false"/>.
    /// </returns>
    public bool Includes(string? tag)
        => _languages.Count == 0
            || string.IsNullOrWhiteSpace(tag)
            || string.Equals(tag, "und", StringComparison.OrdinalIgnoreCase)
            || _languages.Contains(Canonical(tag));

    /// <summary>
    /// Gets a value indicating whether a sidecar is read, judged by the dot-separated segments
    /// of its file name between the media file's name and the extension, such as <c>de.forced</c>
    /// for <c>Episode.de.forced.srt</c>.
    /// </summary>
    /// <param name="nameSegments">The segments, dot-separated; empty for a sidecar named like the media file.</param>
    /// <returns>
    /// <see langword="true"/> when the selection is empty, no segment is a language code, or a
    /// segment names a selected language; otherwise, <see langword="false"/>.
    /// </returns>
    public bool IncludesSidecar(string nameSegments)
    {
        if (_languages.Count == 0)
        {
            return true;
        }

        // "hi" is Hindi and also the usual hearing-impaired flag (Episode.en.hi.srt). Matching
        // any segment reads such a sidecar when English or Hindi is selected.
        var languages = nameSegments
            .Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(KnownLanguage)
            .OfType<string>()
            .ToArray();
        return languages.Length == 0 || languages.Any(_languages.Contains);
    }

    // The 639-2/T code of a code's language part (pt in pt-BR), or null when .NET does not
    // know the language.
    private static string? KnownLanguage(string code)
        => _terminologyCodes.GetValueOrDefault(code.Split('-', '_')[0]);

    // The 639-2/T code of a code's language part; a language .NET does not know keeps its
    // code, lower-cased, so it still matches the same code in a file.
    private static string Canonical(string code)
        => KnownLanguage(code) ?? code.Split('-', '_')[0].ToLowerInvariant();
}
