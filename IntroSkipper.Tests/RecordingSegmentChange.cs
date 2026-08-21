// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.SegmentChanges;

internal sealed class RecordingSegmentChange : ISegmentChange
{
    internal Func<SegmentChangeIntent, SegmentChangeOutcome> Outcome { get; set; }
        = _ => new Accepted(Guid.NewGuid(), [], [new SegmentProjectionResult(Guid.NewGuid(), ProjectionState.Applied)]);

    internal List<SegmentChangeIntent> Intents { get; } = [];

    public Task<SegmentChangeOutcome> ApplyAsync(SegmentChangeIntent intent, CancellationToken cancellationToken = default)
    {
        Intents.Add(intent);
        return Task.FromResult(Outcome(intent));
    }

    public Task<ProjectionStatus> GetProjectionStatusAsync(ProjectionScope scope, CancellationToken cancellationToken = default)
        => Task.FromResult(new ProjectionStatus(scope, []));

    public Task<ProjectionRetryOutcome> RetryProjectionAsync(ProjectionScope scope, CancellationToken cancellationToken = default)
        => Task.FromResult(new ProjectionRetryOutcome(scope, 0, new ProjectionStatus(scope, [])));

    internal static Accepted Accepted(Guid itemId, ProjectionState state, params SegmentValue[] values)
        => new(Guid.NewGuid(), values, [new SegmentProjectionResult(itemId, state)]);
}
