// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using IntroSkipper.SegmentChanges;
using Xunit;

public sealed class TestSegmentChangeHttp
{
    [Fact]
    public void Accepted_UsesStructuredStableProjectionTokens()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var result = SegmentChangeHttp.Accepted(new Accepted(
            Guid.NewGuid(),
            [],
            [
                new SegmentProjectionResult(firstId, ProjectionState.Applied),
                new SegmentProjectionResult(secondId, ProjectionState.Pending)
            ]));

        var response = Assert.IsType<SegmentChangeAcceptedResponse>(result.Value);
        Assert.Equal("Applied", response.Projections[0].Status);
        Assert.Equal("Pending", response.Projections[1].Status);
        Assert.Equal(firstId, response.Projections[0].ItemId);
        Assert.Equal(secondId, response.Projections[1].ItemId);
    }
}
