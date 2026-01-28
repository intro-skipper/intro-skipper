// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;

namespace IntroSkipper.Data;

/// <summary>
/// Union-Find (Disjoint Set Union) data structure for clustering episodes
/// that share a common intro or credits sequence.
/// Tracks the first appearance (lowest episode number) within each cluster.
/// </summary>
public class EpisodeCluster
{
    private readonly Dictionary<Guid, Guid> _parent = [];
    private readonly Dictionary<Guid, int> _rank = [];
    private readonly Dictionary<Guid, int> _episodeNumber = [];

    /// <summary>
    /// Registers an episode with its episode number.
    /// Must be called before using Union or Find operations for this episode.
    /// </summary>
    /// <param name="episodeId">The episode's unique identifier.</param>
    /// <param name="episodeNumber">The episode number within its season.</param>
    public void Register(Guid episodeId, int episodeNumber)
    {
        if (_parent.ContainsKey(episodeId))
        {
            return;
        }

        _parent[episodeId] = episodeId;
        _rank[episodeId] = 0;
        _episodeNumber[episodeId] = episodeNumber;
    }

    /// <summary>
    /// Finds the root representative of the cluster containing the given episode.
    /// Uses path compression for efficiency.
    /// </summary>
    /// <param name="episodeId">The episode's unique identifier.</param>
    /// <returns>The root representative of the cluster.</returns>
    public Guid Find(Guid episodeId)
    {
        if (!_parent.TryGetValue(episodeId, out var parent))
        {
            return episodeId;
        }

        if (parent != episodeId)
        {
            _parent[episodeId] = Find(parent);
        }

        return _parent[episodeId];
    }

    /// <summary>
    /// Merges two episodes into the same cluster.
    /// The representative with the lower episode number becomes the root.
    /// </summary>
    /// <param name="episodeA">First episode's unique identifier.</param>
    /// <param name="episodeB">Second episode's unique identifier.</param>
    public void Union(Guid episodeA, Guid episodeB)
    {
        var rootA = Find(episodeA);
        var rootB = Find(episodeB);

        if (rootA == rootB)
        {
            return;
        }

        // Always make the root with the lower episode number the parent
        // to preserve "first appearance" semantics.
        var episodeNumA = _episodeNumber.GetValueOrDefault(rootA, int.MaxValue);
        var episodeNumB = _episodeNumber.GetValueOrDefault(rootB, int.MaxValue);

        if (episodeNumA < episodeNumB)
        {
            AttachByRank(rootA, rootB);
        }
        else if (episodeNumB < episodeNumA)
        {
            AttachByRank(rootB, rootA);
        }
        else
        {
            // Same episode number; use rank to determine attachment
            if (_rank[rootA] < _rank[rootB])
            {
                _parent[rootA] = rootB;
            }
            else if (_rank[rootA] > _rank[rootB])
            {
                _parent[rootB] = rootA;
            }
            else
            {
                _parent[rootB] = rootA;
                _rank[rootA]++;
            }
        }
    }

    /// <summary>
    /// Gets the first appearance episode ID for the cluster containing the given episode.
    /// The first appearance is the episode with the lowest episode number in the cluster.
    /// </summary>
    /// <param name="episodeId">The episode's unique identifier.</param>
    /// <returns>The episode ID of the first appearance in the cluster.</returns>
    public Guid GetFirstAppearance(Guid episodeId)
    {
        return Find(episodeId);
    }

    /// <summary>
    /// Determines whether the given episode is the first appearance in its cluster.
    /// </summary>
    /// <param name="episodeId">The episode's unique identifier.</param>
    /// <returns>True if this episode is the first appearance; otherwise, false.</returns>
    public bool IsFirstAppearance(Guid episodeId)
    {
        return Find(episodeId) == episodeId;
    }

    /// <summary>
    /// Attaches the secondRoot to the preferredRoot, respecting rank when possible.
    /// </summary>
    private void AttachByRank(Guid preferredRoot, Guid secondRoot)
    {
        // preferredRoot should become the parent, but we also update ranks appropriately
        _parent[secondRoot] = preferredRoot;
        if (_rank[preferredRoot] == _rank[secondRoot])
        {
            _rank[preferredRoot]++;
        }
    }
}
