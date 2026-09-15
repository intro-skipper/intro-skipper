// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Keyframes;
using MediaBrowser.Controller.IO;

/// <summary>
/// In-memory <see cref="IKeyframeManager"/>: one row per item, plus a save count so a test can
/// tell a served row apart from a fresh extraction.
/// </summary>
internal sealed class FakeKeyframeManager : IKeyframeManager
{
    private readonly Dictionary<Guid, KeyframeData> _rows = [];

    public int SaveCount { get; private set; }

    public IReadOnlyList<KeyframeData> GetKeyframeData(Guid itemId)
        => _rows.TryGetValue(itemId, out var data) ? [data] : [];

    public Task SaveKeyframeDataAsync(Guid itemId, KeyframeData data, CancellationToken cancellationToken)
    {
        _rows[itemId] = data;
        SaveCount++;
        return Task.CompletedTask;
    }

    public Task DeleteKeyframeDataAsync(Guid itemId, CancellationToken cancellationToken)
    {
        _rows.Remove(itemId);
        return Task.CompletedTask;
    }

    /// <summary>Seeds a row as Jellyfin's own extraction would have, without counting a save.</summary>
    public void Seed(Guid itemId, KeyframeData data) => _rows[itemId] = data;
}
