// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

internal readonly record struct ExclusionDecision(bool IsExcluded, ExclusionReason Reason, string RuleLabel)
{
    public static ExclusionDecision Included { get; } = new(false, ExclusionReason.None, string.Empty);
}
