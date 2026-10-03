// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Xunit;

/// <summary>
/// Pins the admission decision for automatic segments: which stored rows refuse a
/// candidate, and that only a strict overlap counts. The facade-level counterparts
/// live in <see cref="TestSegmentTombstones"/> (tombstone axis) and TestDatabaseFacades
/// (user-segment and credits-versus-intro axes).
/// </summary>
public sealed class TestAutoSegmentAdmissionPolicy
{
    private static readonly Guid ItemId = Guid.NewGuid();

    // Each expected outcome is the enum member's name: the enum is internal, so a public
    // test method cannot take it as a parameter.
    [Theory]
    [InlineData(10, 20, nameof(AutoSegmentRejection.None))]       // touches the tombstone's end
    [InlineData(-5, 0, nameof(AutoSegmentRejection.None))]        // touches the tombstone's start
    [InlineData(11, 20, nameof(AutoSegmentRejection.None))]       // disjoint with a gap
    [InlineData(9, 20, nameof(AutoSegmentRejection.Tombstone))]   // one tick of shared range
    [InlineData(3, 6, nameof(AutoSegmentRejection.Tombstone))]    // inside the tombstone
    [InlineData(-5, 15, nameof(AutoSegmentRejection.Tombstone))]  // contains the tombstone
    [InlineData(0, 10, nameof(AutoSegmentRejection.Tombstone))]   // identical
    [InlineData(5, 10, nameof(AutoSegmentRejection.Tombstone))]   // shared end, different start
    [InlineData(0, 4, nameof(AutoSegmentRejection.Tombstone))]    // shared start, different end
    public void Check_RefusesOnlyStrictOverlap(long startTicks, long endTicks, string expected)
    {
        var tombstone = Row(AnalysisMode.Introduction, SegmentState.Suppressed, SegmentSource.Chapter);

        Assert.Equal(expected, AutoSegmentAdmissionPolicy.Check(AnalysisMode.Introduction, startTicks, endTicks, [tombstone]).ToString());
    }

    // Human intent of the candidate's own mode refuses it; automatic rows never do (the
    // write replaces them). Against credits, only an active intro refuses, whatever its
    // source; a deleted intro does not, and the rule runs one way only.
    [Theory]
    [InlineData(AnalysisMode.Introduction, AnalysisMode.Introduction, SegmentState.Active, SegmentSource.User, nameof(AutoSegmentRejection.UserSegment))]
    [InlineData(AnalysisMode.Introduction, AnalysisMode.Introduction, SegmentState.Active, SegmentSource.Chapter, nameof(AutoSegmentRejection.None))]
    [InlineData(AnalysisMode.Introduction, AnalysisMode.Commercial, SegmentState.Suppressed, SegmentSource.Chapter, nameof(AutoSegmentRejection.None))]
    [InlineData(AnalysisMode.Introduction, AnalysisMode.Commercial, SegmentState.Active, SegmentSource.User, nameof(AutoSegmentRejection.None))]
    [InlineData(AnalysisMode.Introduction, AnalysisMode.Credits, SegmentState.Active, SegmentSource.BlackFrame, nameof(AutoSegmentRejection.None))]
    [InlineData(AnalysisMode.Credits, AnalysisMode.Introduction, SegmentState.Active, SegmentSource.Chromaprint, nameof(AutoSegmentRejection.Introduction))]
    [InlineData(AnalysisMode.Credits, AnalysisMode.Introduction, SegmentState.Active, SegmentSource.User, nameof(AutoSegmentRejection.Introduction))]
    [InlineData(AnalysisMode.Credits, AnalysisMode.Introduction, SegmentState.Suppressed, SegmentSource.Chromaprint, nameof(AutoSegmentRejection.None))]
    [InlineData(AnalysisMode.Credits, AnalysisMode.Commercial, SegmentState.Active, SegmentSource.Chapter, nameof(AutoSegmentRejection.None))]
    public void Check_RefusesForHumanIntentAndCreditsOverIntro(AnalysisMode candidateMode, AnalysisMode rowMode, SegmentState rowState, SegmentSource rowSource, string expected)
    {
        Assert.Equal(expected, AutoSegmentAdmissionPolicy.Check(candidateMode, 0, 10, [Row(rowMode, rowState, rowSource)]).ToString());
    }

    private static DbSegment Row(AnalysisMode mode, SegmentState state, SegmentSource source)
        => new(ItemId, mode, 0, 10, source) { State = state };
}
