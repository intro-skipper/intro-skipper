// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Manager;
using IntroSkipper.SegmentChanges;
using MediaBrowser.Model.MediaSegments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ControllerSegmentChangeTestHelpers
{
    internal static SegmentChange Create(
        IntroSkipperDatabase database,
        FakeJellyfinSegmentStore store,
        bool projectionEnabled = true)
        => Create(database, new StoreProjectionAdapter(store), projectionEnabled);

    internal static SegmentChange Create(
        IntroSkipperDatabase database,
        ISegmentProjectionAdapter adapter,
        bool projectionEnabled = true)
    {
        var factory = (IDbContextFactory<IntroSkipperDbContext>)typeof(IntroSkipperDatabase)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(field => typeof(IDbContextFactory<IntroSkipperDbContext>).IsAssignableFrom(field.FieldType))
            .GetValue(database)!;
        return new SegmentChange(
            factory,
            database,
            adapter,
            new ProjectionConfiguration(projectionEnabled),
            TimeProvider.System,
            NullLogger<SegmentChange>.Instance);
    }

    private sealed class StoreProjectionAdapter(FakeJellyfinSegmentStore store) : ISegmentProjectionAdapter
    {
        public Task<ExternalSegmentTarget?> ResolveExternalTargetAsync(Guid itemId, Guid externalSegmentId, CancellationToken cancellationToken)
        {
            var segment = store.ExistingSegments.FirstOrDefault(value => value.Id == externalSegmentId);
            return Task.FromResult(segment is null
                ? null
                : new ExternalSegmentTarget(segment.Id, segment.ItemId, segment.Type, segment.StartTicks, segment.EndTicks));
        }

        public async Task ApplyAsync(SegmentProjectionPlan plan, CancellationToken cancellationToken)
        {
            foreach (var operation in plan.ExternalOperations)
            {
                await store.DeleteSegmentAsync(plan.ItemId, operation.ExternalSegmentId, cancellationToken).ConfigureAwait(false);
            }

            await store.ReplaceSegmentsAsync(
                plan.ItemId,
                plan.Segments.Select(value => new MediaSegmentDto
                {
                    Id = value.Id,
                    ItemId = plan.ItemId,
                    Type = value.Type,
                    StartTicks = value.StartTicks,
                    EndTicks = value.EndTicks
                }).ToList(),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ProjectionConfiguration(bool enabled) : ISegmentProjectionConfiguration
    {
        public event EventHandler<bool>? EnabledChanged
        {
            add { }
            remove { }
        }

        public bool Enabled { get; } = enabled;
    }
}
