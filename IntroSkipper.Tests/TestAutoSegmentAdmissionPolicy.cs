// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Xunit;
using static IntroSkipper.Tests.DatabaseTestHelpers;

/// <summary>
/// Pins the admission decision for automatic segments: which stored rows refuse a
/// candidate, and how far a candidate may reach past a tombstone and still be refused as a
/// re-detection of the deleted range. The facade-level counterparts
/// live in <see cref="TestSegmentTombstones"/> (tombstone axis) and TestDatabaseFacades
/// (user-segment and credits-versus-intro axes).
/// </summary>
public sealed class TestAutoSegmentAdmissionPolicy
{
    private const long Tolerance = AutoSegmentAdmissionPolicy.TombstoneToleranceTicks;

    private static readonly Guid ItemId = Guid.NewGuid();

    private static readonly long DeletedStart = Ticks(100);

    private static readonly long DeletedEnd = Ticks(160);

    // Candidates against a tombstone at 100-160 s. Each expected outcome is the enum
    // member's name: the enum is internal, so a public test method cannot take it as a
    // parameter.
    public static TheoryData<long, long, string> TombstoneCandidates => new()
    {
        { DeletedStart, DeletedEnd, nameof(AutoSegmentRejection.Tombstone) },                         // identical
        { DeletedStart + Ticks(2), DeletedEnd + Ticks(1), nameof(AutoSegmentRejection.Tombstone) },   // shifted within the tolerance
        { DeletedStart + Ticks(20), DeletedEnd - Ticks(20), nameof(AutoSegmentRejection.Tombstone) }, // a shorter piece of the deleted range
        { DeletedStart - Tolerance, DeletedEnd + Tolerance, nameof(AutoSegmentRejection.Tombstone) }, // each end the tolerance outside
        { DeletedStart - Tolerance, DeletedStart + 1, nameof(AutoSegmentRejection.Tombstone) },       // one shared tick
        { DeletedStart, DeletedEnd + Tolerance + 1, nameof(AutoSegmentRejection.None) },              // reaches past the end: a correction
        { DeletedStart - Tolerance - 1, DeletedEnd, nameof(AutoSegmentRejection.None) },              // reaches past the start
        { DeletedEnd + Ticks(10), DeletedEnd + Ticks(20), nameof(AutoSegmentRejection.None) },        // disjoint
        { DeletedEnd, DeletedEnd + Ticks(2), nameof(AutoSegmentRejection.None) },                     // touches the end
        { DeletedStart - Ticks(2), DeletedStart, nameof(AutoSegmentRejection.None) },                 // touches the start
    };

    [Theory]
    [MemberData(nameof(TombstoneCandidates))]
    public void Check_TombstoneRefusesOnlyReDetectionsOfTheDeletedRange(long startTicks, long endTicks, string expected)
    {
        var tombstone = new DbSegment(ItemId, AnalysisMode.Introduction, DeletedStart, DeletedEnd, SegmentSource.Chromaprint) { State = SegmentState.Suppressed };

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
