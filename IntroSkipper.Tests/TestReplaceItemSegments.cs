// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Xunit;

/// <summary>
/// Facade tests for <see cref="IIntroSkipperDatabase.ReplaceItemSegmentsAsync"/>: mode
/// scoping, prior-row return for compensation, commercial multi-row support, and the
/// argument guards covering both of the segment table's uniqueness rules, each of which
/// must leave the prior rows intact.
/// </summary>
public sealed class TestReplaceItemSegments
{
    [Fact]
    public async Task ReplacesOnlyRequestedModes_AndReturnsPriorRows()
    {
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(10, 20)), AnalysisMode.Introduction, configHash: "cfg-intro");
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(1200, 1260)), AnalysisMode.Credits, isUserProvided: true, configHash: "cfg-credits");
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(300, 330)), AnalysisMode.Commercial);
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(600, 630)), AnalysisMode.Commercial);

        var replacement = new DbSegment(new Segment(itemId, new TimeRange(15, 25)), AnalysisMode.Introduction, isUserProvided: true);
        var priors = await database.ReplaceItemSegmentsAsync(itemId, [AnalysisMode.Introduction, AnalysisMode.Commercial], [replacement]);

        Assert.Equal(3, priors.Count);
        Assert.Single(priors, row => row.Type == AnalysisMode.Introduction && row.ConfigHash == "cfg-intro");
        Assert.Equal(2, priors.Count(row => row.Type == AnalysisMode.Commercial));

        var rows = await database.GetSegmentsAsync(itemId);
        Assert.Equal(2, rows.Count);
        var intro = Assert.Single(rows, row => row.Type == AnalysisMode.Introduction);
        Assert.Equal(15, intro.Start);
        Assert.True(intro.IsUserProvided);
        var credits = Assert.Single(rows, row => row.Type == AnalysisMode.Credits);
        Assert.Equal("cfg-credits", credits.ConfigHash);
        Assert.True(credits.IsUserProvided);
    }

    [Fact]
    public async Task StoresMultipleCommercialRows()
    {
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();

        var priors = await database.ReplaceItemSegmentsAsync(
            itemId,
            [AnalysisMode.Commercial],
            [
                new DbSegment(new Segment(itemId, new TimeRange(10, 20)), AnalysisMode.Commercial, isUserProvided: true),
                new DbSegment(new Segment(itemId, new TimeRange(30, 40)), AnalysisMode.Commercial, isUserProvided: true),
            ]);

        Assert.Empty(priors);
        var rows = await database.GetSegmentsAsync(itemId);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task EpsilonEquivalentCommercialInput_Throws_AndLeavesPriorRowsIntact()
    {
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.UpdateTimestampAsync(
            new Segment(itemId, new TimeRange(100, 110)),
            AnalysisMode.Commercial,
            configHash: "cfg-keep");

        await Assert.ThrowsAsync<ArgumentException>(() => database.ReplaceItemSegmentsAsync(
            itemId,
            [AnalysisMode.Commercial],
            [
                new DbSegment(new Segment(itemId, new TimeRange(10, 20)), AnalysisMode.Commercial),
                new DbSegment(new Segment(itemId, new TimeRange(10.0005, 20.0005)), AnalysisMode.Commercial),
            ]));

        var row = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal(100, row.Start);
        Assert.Equal("cfg-keep", row.ConfigHash);
    }

    [Fact]
    public async Task RestoringPriorRows_PreservesFlagsAndHashes()
    {
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(10, 20)), AnalysisMode.Introduction, configHash: "cfg-auto");
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(500, 560)), AnalysisMode.Credits, isUserProvided: true, configHash: "cfg-user");

        var modes = new[] { AnalysisMode.Introduction, AnalysisMode.Credits };
        var priors = await database.ReplaceItemSegmentsAsync(
            itemId,
            modes,
            [new DbSegment(new Segment(itemId, new TimeRange(1, 2)), AnalysisMode.Introduction, isUserProvided: true)]);

        // The facade returns detached copies, so the prior rows restore directly.
        await database.ReplaceItemSegmentsAsync(itemId, modes, priors);

        var rows = await database.GetSegmentsAsync(itemId);
        Assert.Equal(2, rows.Count);
        var intro = Assert.Single(rows, row => row.Type == AnalysisMode.Introduction);
        Assert.False(intro.IsUserProvided);
        Assert.Equal("cfg-auto", intro.ConfigHash);
        var credits = Assert.Single(rows, row => row.Type == AnalysisMode.Credits);
        Assert.True(credits.IsUserProvided);
        Assert.Equal("cfg-user", credits.ConfigHash);
    }

    [Fact]
    public async Task ReplacesUsingRowsLoadedFromDatabase_WithGeneratedIds()
    {
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.UpdateTimestampAsync(
            new Segment(itemId, new TimeRange(10, 20)),
            AnalysisMode.Introduction,
            isUserProvided: true,
            configHash: "cfg");
        var loadedRows = await database.GetSegmentsAsync(itemId);
        Assert.NotEqual(0, Assert.Single(loadedRows).Id);

        var priors = await database.ReplaceItemSegmentsAsync(
            itemId,
            [AnalysisMode.Introduction],
            loadedRows);

        Assert.Single(priors);
        var replacement = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal(10, replacement.Start);
        Assert.True(replacement.IsUserProvided);
        Assert.Equal("cfg", replacement.ConfigHash);
    }

    [Fact]
    public async Task DuplicateNonCommercialInput_Throws_AndLeavesPriorRowsIntact()
    {
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(10, 20)), AnalysisMode.Introduction, configHash: "cfg-keep");

        // Rejected before the transaction opens: IX_DbSegment_NonCommercial_Unique would
        // otherwise fail the insert mid-transaction and surface as a server fault, so the
        // facade treats a second row of one mode as a caller error, exactly like the
        // commercial equivalence guard.
        await Assert.ThrowsAsync<ArgumentException>(() => database.ReplaceItemSegmentsAsync(
            itemId,
            [AnalysisMode.Introduction],
            [
                new DbSegment(new Segment(itemId, new TimeRange(1, 2)), AnalysisMode.Introduction, isUserProvided: true),
                new DbSegment(new Segment(itemId, new TimeRange(3, 4)), AnalysisMode.Introduction, isUserProvided: true),
            ]));

        var row = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal("cfg-keep", row.ConfigHash);
        Assert.Equal(10, row.Start);
    }

    [Fact]
    public async Task RejectsMismatchedItemId_AndModeOutsideSet_AndEmptyModes()
    {
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var foreignRow = new DbSegment(new Segment(Guid.NewGuid(), new TimeRange(1, 2)), AnalysisMode.Introduction);
        var outsideMode = new DbSegment(new Segment(itemId, new TimeRange(1, 2)), AnalysisMode.Credits);

        await Assert.ThrowsAsync<ArgumentException>(() => database.ReplaceItemSegmentsAsync(itemId, [AnalysisMode.Introduction], [foreignRow]));
        await Assert.ThrowsAsync<ArgumentException>(() => database.ReplaceItemSegmentsAsync(itemId, [AnalysisMode.Introduction], [outsideMode]));
        await Assert.ThrowsAsync<ArgumentException>(() => database.ReplaceItemSegmentsAsync(itemId, [], []));
    }
}
