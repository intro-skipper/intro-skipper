// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using IntroSkipper.Remote;
using SegmentScanner.Contracts;

namespace IntroSkipper.Data;

/// <summary>Tracks operations actually executing, separately from queued batch membership.</summary>
public sealed class ScannerActivityTracker
{
    private readonly ConcurrentDictionary<Guid, ScannerQueueActivity> _active = new();

    /// <summary>Gets the current per-file operations.</summary>
    public IReadOnlyList<ScannerQueueActivity> Active => _active.Values.DistinctBy(item => (item.ItemId, item.Mode)).ToArray();

    /// <summary>Begins a file operation, removed when the returned scope is disposed.</summary>
    /// <param name="episode">The file being processed.</param>
    /// <param name="mode">The analysis mode.</param>
    /// <param name="phase">The operation shown to the user.</param>
    /// <returns>The activity lifetime.</returns>
    public IDisposable Begin(QueuedEpisode episode, AnalysisMode mode, string phase)
    {
        var id = Guid.NewGuid();
        _active[id] = new ScannerQueueActivity(episode.EpisodeId, RemoteModes.ToRemote(mode), phase);
        return new ActivityScope(() => _active.TryRemove(id, out _));
    }

    private sealed class ActivityScope(Action finish) : IDisposable
    {
        private Action? _finish = finish;

        public void Dispose() => Interlocked.Exchange(ref _finish, null)?.Invoke();
    }
}
