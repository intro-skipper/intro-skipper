// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using IntroSkipper.Data;
using Xunit;

public sealed class TestChromaprintConstants
{
    [Fact]
    public void SampleDuration_MatchesChromaprintHopDuration()
    {
        // 4096 / 11025 / 3 ≈ 0.12383
        Assert.Equal(4096.0 / 11025.0 / 3.0, ChromaprintConstants.SampleDuration);
    }
}
