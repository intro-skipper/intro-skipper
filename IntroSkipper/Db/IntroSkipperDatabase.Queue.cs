// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;
using SegmentScanner.Contracts;

namespace IntroSkipper.Db;

public sealed partial class IntroSkipperDatabase
{
    internal async Task<(List<DbRemoteJob> Jobs, int PendingCount)> GetRemoteQueueAsync(CancellationToken token)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var now = DateTime.UtcNow;
        var pending = db.RemoteJobs.AsNoTracking().Where(job => job.State == "Pending" || (job.State == "Leased" && job.LeaseExpiresUtc <= now));
        var count = await pending.CountAsync(token).ConfigureAwait(false);
        var rows = await pending.OrderBy(job => job.CreatedUtc).Take(100).ToListAsync(token).ConfigureAwait(false);
        var running = await db.RemoteJobs.AsNoTracking().Where(job => job.State == "Leased" && job.LeaseExpiresUtc > now).OrderBy(job => job.CreatedUtc).ToListAsync(token).ConfigureAwait(false);
        return ([.. running, .. rows], count);
    }

    internal async Task CancelRemoteQueueAsync(bool running, CancellationToken token)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var now = DateTime.UtcNow;
        var jobs = running
            ? db.RemoteJobs.Where(job => job.State == "Leased")
            : db.RemoteJobs.Where(job => job.State == "Pending" || (job.State == "Leased" && job.LeaseExpiresUtc <= now));
        await jobs.ExecuteUpdateAsync(
            setters => setters
            .SetProperty(job => job.State, "Cancelled")
            .SetProperty(job => job.LeaseTokenHash, (string?)null)
            .SetProperty(job => job.LeaseGeneration, job => job.LeaseGeneration + 1)
            .SetProperty(job => job.LeaseExpiresUtc, now)
            .SetProperty(job => job.UpdatedUtc, now),
            token).ConfigureAwait(false);
    }

    internal async Task<bool> ReportRemoteProgressAsync(Guid jobId, ScannerQueueProgressRequest progress, TimeSpan leaseDuration, CancellationToken token)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var row = await db.RemoteJobs.AsNoTracking().SingleOrDefaultAsync(job => job.Id == jobId, token).ConfigureAwait(false);
        if (row is null || !LeaseMatches(row, progress.Lease, DateTime.UtcNow))
        {
            return false;
        }

        var snapshot = WireJson.Read<JobSnapshot>(row.SnapshotJson);
        var itemIds = snapshot.Items.Select(item => item.ItemId).ToHashSet();
        if (progress.Active.Any(item => !itemIds.Contains(item.ItemId) || !snapshot.Modes.Contains(item.Mode))
            || progress.Completed.Any(item => !itemIds.Contains(item)))
        {
            throw new InvalidDataException("Progress references work outside the leased job.");
        }

        return await RenewRemoteLeaseAsync(jobId, progress.Lease.LeaseToken, progress.Lease.LeaseGeneration, leaseDuration, token).ConfigureAwait(false);
    }
}
