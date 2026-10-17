// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using SegmentScanner.Contracts;

namespace IntroSkipper.Remote;

/// <summary>Keeps recent lease-validated activity; job ownership remains in the database.</summary>
public sealed class ScannerQueueProgressStore
{
    private readonly ConcurrentDictionary<Guid, Progress> _jobs = new();

    internal void Report(Guid jobId, ScannerQueueProgressRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        _jobs[jobId] = new Progress(request.Lease.LeaseGeneration, request.Active, request.Completed, now);
        foreach (var item in _jobs.Where(item => item.Value.UpdatedUtc < now.AddMinutes(-2)))
        {
            _jobs.TryRemove(item.Key, out _);
        }
    }

    internal Progress? Get(Guid jobId, int generation)
        => _jobs.TryGetValue(jobId, out var progress) && progress.Generation == generation && progress.UpdatedUtc >= DateTimeOffset.UtcNow.AddSeconds(-15)
            ? progress : null;

    internal sealed record Progress(int Generation, List<ScannerQueueActivity> Active, List<Guid> Completed, DateTimeOffset UpdatedUtc);
}
