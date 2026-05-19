// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;

namespace IntroSkipper.FFmpeg;

internal static class LegacyDetectionCacheFileName
{
    internal enum LegacyCacheKind
    {
        Unsupported,
        Fingerprint,
        CreditFingerprint,
        Silence,
        BlackFrameRange,
        BlackFrameCredits,
        Keyframe,
    }

    public static LegacyParseResult? TryParse(string fileName)
    {
        if (fileName.Length < 32 || !Guid.TryParseExact(fileName[..32], "N", out var itemId))
        {
            return null;
        }

        if (fileName.Length == 32)
        {
            return new(itemId, LegacyCacheKind.Fingerprint, 0, 0);
        }

        if (fileName[32] != '-')
        {
            return new(itemId, LegacyCacheKind.Unsupported, 0, 0);
        }

        var suffix = fileName[33..];
        if (suffix == "credits")
        {
            return new(itemId, LegacyCacheKind.CreditFingerprint, 0, 0);
        }

        if (TryParseRangeSuffix(suffix, "silence-", "-v2", out var start, out var end))
        {
            return new(itemId, LegacyCacheKind.Silence, start, end);
        }

        if (TryParseRangeSuffix(suffix, "blackframes-", "-v1", out start, out end))
        {
            return new(itemId, LegacyCacheKind.BlackFrameRange, start, end);
        }

        if (suffix.StartsWith("blackframes-", StringComparison.Ordinal) && suffix.EndsWith("-alt", StringComparison.Ordinal))
        {
            var inner = suffix["blackframes-".Length..^"-alt".Length];
            if (double.TryParse(inner, CultureInfo.InvariantCulture, out start))
            {
                return new(itemId, LegacyCacheKind.BlackFrameCredits, start, 0);
            }
        }

        if (TryParseRangeSuffix(suffix, "keyframes-", "-v1", out start, out end))
        {
            return new(itemId, LegacyCacheKind.Keyframe, start, end);
        }

        return new(itemId, LegacyCacheKind.Unsupported, 0, 0);
    }

    private static bool TryParseRangeSuffix(string suffix, string prefix, string versionSuffix, out double start, out double end)
    {
        start = 0;
        end = 0;

        if (!suffix.StartsWith(prefix, StringComparison.Ordinal) || !suffix.EndsWith(versionSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var inner = suffix[prefix.Length..^versionSuffix.Length];
        var parts = inner.Split('-');
        return parts.Length == 2 &&
            double.TryParse(parts[0], CultureInfo.InvariantCulture, out start) &&
            double.TryParse(parts[1], CultureInfo.InvariantCulture, out end);
    }

    internal sealed record LegacyParseResult(Guid ItemId, LegacyCacheKind Kind, double Start, double End);
}
