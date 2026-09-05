// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>Result of an exclusion-policy check.</summary>
/// <param name="IsExcluded">Whether the item is excluded from analysis.</param>
/// <param name="RuleLabel">The matched rule, for logging: the excluded name, or <c>PathExclusions</c>; empty when included.</param>
internal readonly record struct ExclusionDecision(bool IsExcluded, string RuleLabel)
{
    public static ExclusionDecision Included { get; } = new(false, string.Empty);
}
