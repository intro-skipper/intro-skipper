// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
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
