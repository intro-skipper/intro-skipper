// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Minimal <see cref="IDbContextFactory{TContext}"/> for tests: creates contexts via
/// the supplied delegate, typically a path-based context constructor over a temp file.
/// </summary>
/// <typeparam name="TContext">Context type.</typeparam>
/// <param name="create">Delegate that creates a context.</param>
internal sealed class TestDbContextFactory<TContext>(Func<TContext> create) : IDbContextFactory<TContext>
    where TContext : DbContext
{
    public TContext CreateDbContext() => create();
}
