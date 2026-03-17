// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestDbSegmentStorage
{
    [Fact]
    public void AllowsMultipleCommercialSegments()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<IntroSkipperDbContext>()
            .UseSqlite(connection)
            .Options;

        var itemId = Guid.NewGuid();

        using (var db = new IntroSkipperDbContext(options))
        {
            db.Database.EnsureCreated();

            var firstSegment = new DbSegment(
                new Segment(itemId, new TimeRange(0, 10)),
                AnalysisMode.Commercial);
            var secondSegment = new DbSegment(
                new Segment(itemId, new TimeRange(20, 30)),
                AnalysisMode.Commercial);

            db.DbSegment.AddRange(firstSegment, secondSegment);
            db.SaveChanges();
        }

        using (var db = new IntroSkipperDbContext(options))
        {
            var count = db.DbSegment.Count(segment => segment.ItemId == itemId && segment.Type == AnalysisMode.Commercial);
            Assert.Equal(2, count);
        }
    }

    [Fact]
    public void NonCommercialUniqueIndexPreventsInsertingDuplicateForSameItemAndType()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<IntroSkipperDbContext>()
            .UseSqlite(connection)
            .Options;

        var itemId = Guid.NewGuid();

        using (var db = new IntroSkipperDbContext(options))
        {
            db.Database.EnsureCreated();

            db.DbSegment.Add(new DbSegment(
                new Segment(itemId, new TimeRange(0, 10)),
                AnalysisMode.Introduction));
            db.SaveChanges();
        }

        using (var db = new IntroSkipperDbContext(options))
        {
            // Attempting to insert a second Introduction segment for the same item must
            // violate the non-commercial unique index and throw a DbUpdateException.
            db.DbSegment.Add(new DbSegment(
                new Segment(itemId, new TimeRange(5, 15)),
                AnalysisMode.Introduction));

            Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        }
    }

    [Fact]
    public void NonCommercialUniqueIndexAllowsSameModeForDifferentItems()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<IntroSkipperDbContext>()
            .UseSqlite(connection)
            .Options;

        var itemIdA = Guid.NewGuid();
        var itemIdB = Guid.NewGuid();

        using (var db = new IntroSkipperDbContext(options))
        {
            db.Database.EnsureCreated();

            db.DbSegment.AddRange(
                new DbSegment(new Segment(itemIdA, new TimeRange(0, 10)), AnalysisMode.Introduction),
                new DbSegment(new Segment(itemIdB, new TimeRange(0, 10)), AnalysisMode.Introduction));

            // No exception — different items may have the same mode.
            db.SaveChanges();
        }

        using (var db = new IntroSkipperDbContext(options))
        {
            Assert.Equal(1, db.DbSegment.Count(s => s.ItemId == itemIdA && s.Type == AnalysisMode.Introduction));
            Assert.Equal(1, db.DbSegment.Count(s => s.ItemId == itemIdB && s.Type == AnalysisMode.Introduction));
        }
    }

    [Fact]
    public void UserProvidedFlagIsPreservedOnDbSegment()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<IntroSkipperDbContext>()
            .UseSqlite(connection)
            .Options;

        var itemId = Guid.NewGuid();

        using (var db = new IntroSkipperDbContext(options))
        {
            db.Database.EnsureCreated();

            db.DbSegment.Add(new DbSegment(
                new Segment(itemId, new TimeRange(10, 60)),
                AnalysisMode.Introduction,
                isUserProvided: true));
            db.SaveChanges();
        }

        using (var db = new IntroSkipperDbContext(options))
        {
            var segment = db.DbSegment
                .Single(s => s.ItemId == itemId && s.Type == AnalysisMode.Introduction);

            Assert.True(segment.IsUserProvided);
        }
    }
}
