// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Xunit;

/// <summary>
/// Decision-table tests for <see cref="SegmentWriteDecision"/> — the pure function
/// guarding non-commercial segment writes. No database is involved; the DB-level
/// invariant tests in <see cref="TestDatabaseFacades"/> double as integration coverage
/// of the same rules through <c>UpdateTimestampAsync</c>.
/// </summary>
public sealed class TestSegmentWriteDecision
{
    private static readonly Guid _itemId = Guid.NewGuid();

    [Theory]
    // (write isUserProvided, existing segment: null = none / false = auto / true = user) → persist?
    [InlineData(false, null, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)] // analysis result never overwrites a user-provided segment
    [InlineData(true, null, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void ShouldPersist_UserPrecedenceMatrix(bool isUserProvided, bool? existingIsUserProvided, bool expectPersist)
    {
        DbSegment[] existing = existingIsUserProvided is null
            ? []
            : [CreateDbSegment(20, 80, AnalysisMode.Introduction, existingIsUserProvided.Value)];

        var verdict = SegmentWriteDecision.ShouldPersist(
            existing,
            storedIntroduction: null,
            new Segment(_itemId, new TimeRange(10, 60)),
            AnalysisMode.Introduction,
            isUserProvided);

        Assert.Equal(
            expectPersist ? SegmentWriteDecision.Verdict.Persist : SegmentWriteDecision.Verdict.SkipUserProvidedPrecedence,
            verdict);
    }

    [Fact]
    public void ShouldPersist_AnalysisWrite_SkipsWhenAnyExistingSegmentIsUserProvided()
    {
        var existing = new List<DbSegment>
        {
            CreateDbSegment(0, 30, AnalysisMode.Introduction, isUserProvided: false),
            CreateDbSegment(40, 70, AnalysisMode.Introduction, isUserProvided: true),
        };

        var verdict = SegmentWriteDecision.ShouldPersist(
            existing,
            storedIntroduction: null,
            new Segment(_itemId, new TimeRange(10, 60)),
            AnalysisMode.Introduction,
            isUserProvided: false);

        Assert.Equal(SegmentWriteDecision.Verdict.SkipUserProvidedPrecedence, verdict);
    }

    [Theory]
    // (intro start/end, credits start/end) → persist? The guard uses strict < on both
    // sides, so segments merely touching at a boundary do not overlap.
    [InlineData(0, 90, 60, 1440, false)]  // overlap → skipped
    [InlineData(0, 90, 10, 50, false)]    // credits inside intro → skipped
    [InlineData(0, 90, 90, 200, true)]    // touch at intro end → persisted
    [InlineData(100, 200, 0, 100, true)]  // touch at intro start → persisted
    [InlineData(0, 90, 1200, 1440, true)] // no overlap → persisted
    public void ShouldPersist_AutoCredits_OverlapGuardUsesStrictComparisons(
        double introStart, double introEnd, double creditsStart, double creditsEnd, bool expectPersist)
    {
        var verdict = SegmentWriteDecision.ShouldPersist(
            [],
            CreateDbSegment(introStart, introEnd, AnalysisMode.Introduction, isUserProvided: false),
            new Segment(_itemId, new TimeRange(creditsStart, creditsEnd)),
            AnalysisMode.Credits,
            isUserProvided: false);

        Assert.Equal(
            expectPersist ? SegmentWriteDecision.Verdict.Persist : SegmentWriteDecision.Verdict.SkipCreditsOverlapIntro,
            verdict);
    }

    [Fact]
    public void ShouldPersist_UserProvidedCredits_BypassOverlapGuard()
    {
        // Even with an overlapping stored introduction supplied, a user-provided
        // credits segment is persisted.
        var verdict = SegmentWriteDecision.ShouldPersist(
            [],
            CreateDbSegment(0, 90, AnalysisMode.Introduction, isUserProvided: false),
            new Segment(_itemId, new TimeRange(60, 1440)),
            AnalysisMode.Credits,
            isUserProvided: true);

        Assert.Equal(SegmentWriteDecision.Verdict.Persist, verdict);
    }

    [Fact]
    public void ShouldPersist_AutoCredits_PersistsWhenNoIntroductionIsStored()
    {
        var verdict = SegmentWriteDecision.ShouldPersist(
            [],
            storedIntroduction: null,
            new Segment(_itemId, new TimeRange(60, 1440)),
            AnalysisMode.Credits,
            isUserProvided: false);

        Assert.Equal(SegmentWriteDecision.Verdict.Persist, verdict);
    }

    [Theory]
    [InlineData(AnalysisMode.Introduction)]
    [InlineData(AnalysisMode.Preview)]
    [InlineData(AnalysisMode.Recap)]
    public void ShouldPersist_NonCreditsModes_AreNotSubjectToOverlapGuard(AnalysisMode mode)
    {
        // Even if a caller supplied a stored introduction overlapping the new segment,
        // the guard only applies to auto-detected credits.
        var verdict = SegmentWriteDecision.ShouldPersist(
            [],
            CreateDbSegment(0, 90, AnalysisMode.Introduction, isUserProvided: false),
            new Segment(_itemId, new TimeRange(60, 120)),
            mode,
            isUserProvided: false);

        Assert.Equal(SegmentWriteDecision.Verdict.Persist, verdict);
    }

    [Theory]
    [InlineData(AnalysisMode.Credits, false, true)]
    [InlineData(AnalysisMode.Credits, true, false)]
    [InlineData(AnalysisMode.Introduction, false, false)]
    [InlineData(AnalysisMode.Introduction, true, false)]
    [InlineData(AnalysisMode.Preview, false, false)]
    [InlineData(AnalysisMode.Recap, false, false)]
    [InlineData(AnalysisMode.Commercial, false, false)]
    public void RequiresStoredIntroduction_OnlyForAutoDetectedCredits(AnalysisMode mode, bool isUserProvided, bool expected)
    {
        Assert.Equal(expected, SegmentWriteDecision.RequiresStoredIntroduction(mode, isUserProvided));
    }

    private static DbSegment CreateDbSegment(double start, double end, AnalysisMode mode, bool isUserProvided)
        => new(new Segment(_itemId, new TimeRange(start, end)), mode, isUserProvided);
}
