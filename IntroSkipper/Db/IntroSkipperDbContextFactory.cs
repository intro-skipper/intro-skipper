// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IntroSkipper.Db;

/// <summary>
/// IntroSkipperDbContext factory.
/// </summary>
public class IntroSkipperDbContextFactory : IDesignTimeDbContextFactory<IntroSkipperDbContext>
{
    /// <inheritdoc/>
    public IntroSkipperDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<IntroSkipperDbContext>();
        optionsBuilder.UseSqlite("Data Source=introskipper.db")
                      .EnableSensitiveDataLogging(false);

        return new IntroSkipperDbContext(optionsBuilder.Options);
    }
}
