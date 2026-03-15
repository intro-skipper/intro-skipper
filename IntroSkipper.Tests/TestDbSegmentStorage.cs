// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>

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
}
