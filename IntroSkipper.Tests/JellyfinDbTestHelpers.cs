// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// A real <see cref="JellyfinDbContext"/> over a temp-file SQLite database, so store
/// tests run against the same EF model the server uses. The schema is created once on
/// construction; the database and its sidecar files are deleted on dispose.
/// The omitted SQLite-provider model hooks only affect DateTime conversion and
/// RETURNING-clause suppression — neither applies to the MediaSegments table.
/// </summary>
internal sealed class TempJellyfinDb : IDisposable
{
    private readonly string _dbPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="TempJellyfinDb"/> class.
    /// </summary>
    /// <param name="lockingBehavior">
    /// Locking behavior shared by all contexts from <see cref="Factory"/>; defaults to
    /// <see cref="NoLockBehavior"/> (the server's default).
    /// </param>
    internal TempJellyfinDb(IEntityFrameworkCoreLockingBehavior? lockingBehavior = null)
    {
        _dbPath = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-jellyfin.db");
        var behavior = lockingBehavior ?? new NoLockBehavior(NullLogger<NoLockBehavior>.Instance);
        var optionsBuilder = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite($"Data Source={_dbPath}");
        behavior.Initialise(optionsBuilder);
        var options = optionsBuilder.Options;
        var databaseProvider = new FakeJellyfinDatabaseProvider();

        Factory = new TestDbContextFactory<JellyfinDbContext>(() => new JellyfinDbContext(
            options,
            NullLogger<JellyfinDbContext>.Instance,
            databaseProvider,
            behavior));

        using var context = Factory.CreateDbContext();
        context.Database.EnsureCreated();
    }

    internal TestDbContextFactory<JellyfinDbContext> Factory { get; }

    public void Dispose() => DatabaseTestHelpers.DeleteSqliteFiles(_dbPath);

    private sealed class FakeJellyfinDatabaseProvider : IJellyfinDatabaseProvider
    {
        public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

        public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
        {
        }

        public void OnModelCreating(ModelBuilder modelBuilder)
        {
        }

        public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
        }

        public Task RunScheduledOptimisation(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RunShutdownTask(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> MigrationBackupFast(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RestoreBackupFast(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteBackup(string key) => throw new NotSupportedException();

        public Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames) => throw new NotSupportedException();
    }
}
