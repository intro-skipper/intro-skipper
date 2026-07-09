// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Minimal <see cref="IDbContextFactory{TContext}"/> over a context-creating delegate.
/// This is the <b>ungated</b> factory used by the database facades: the facades own
/// database initialization, so they must be able to create contexts while their own
/// initialization gate is still pending — handing them the gated factory registered
/// in dependency injection would deadlock the gate against itself.
/// </summary>
/// <typeparam name="TContext">Context type.</typeparam>
internal sealed class DelegateDbContextFactory<TContext> : IDbContextFactory<TContext>
    where TContext : DbContext
{
    private readonly Func<TContext> _createContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="DelegateDbContextFactory{TContext}"/> class.
    /// </summary>
    /// <param name="createContext">Delegate creating a fresh context per call.</param>
    internal DelegateDbContextFactory(Func<TContext> createContext)
    {
        ArgumentNullException.ThrowIfNull(createContext);
        _createContext = createContext;
    }

    /// <inheritdoc/>
    public TContext CreateDbContext() => _createContext();

    /// <inheritdoc/>
    /// <remarks>
    /// Explicit override of the default interface method for clarity: context creation
    /// is purely synchronous here, so the async surface completes synchronously.
    /// </remarks>
    public Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_createContext());
}
