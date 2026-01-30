// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

namespace IntroSkipper.Data;

/// <summary>
/// Represents a cluster of episodes that share common intro/credits characteristics.
/// Used for tracking segments across similar episodes in a season.
/// </summary>
public class EpisodeCluster
{
    private readonly List<Guid> _episodeIds = [];
    private double _startSum;
    private double _endSum;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeCluster"/> class.
    /// </summary>
    public EpisodeCluster()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeCluster"/> class with an initial segment.
    /// </summary>
    /// <param name="segment">The initial segment to add to the cluster.</param>
    public EpisodeCluster(Segment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        Add(segment);
    }

    /// <summary>
    /// Gets the list of episode IDs in this cluster.
    /// </summary>
    public IReadOnlyList<Guid> EpisodeIds => _episodeIds;

    /// <summary>
    /// Gets the count of episodes in the cluster.
    /// </summary>
    public int Count => _episodeIds.Count;

    /// <summary>
    /// Gets the average start time of segments in this cluster.
    /// </summary>
    public double AverageStart => Count > 0 ? _startSum / Count : 0;

    /// <summary>
    /// Gets the average end time of segments in this cluster.
    /// </summary>
    public double AverageEnd => Count > 0 ? _endSum / Count : 0;

    /// <summary>
    /// Gets the average duration of segments in this cluster.
    /// </summary>
    public double AverageDuration => AverageEnd - AverageStart;

    /// <summary>
    /// Adds a segment to this cluster.
    /// </summary>
    /// <param name="segment">The segment to add.</param>
    /// <exception cref="ArgumentNullException">Thrown when segment is null.</exception>
    public void Add(Segment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        _episodeIds.Add(segment.EpisodeId);
        _startSum += segment.Start;
        _endSum += segment.End;
    }

    /// <summary>
    /// Determines if a segment can be added to this cluster based on tolerance.
    /// </summary>
    /// <param name="segment">The segment to check.</param>
    /// <param name="tolerance">The maximum allowed difference from cluster averages.</param>
    /// <returns>True if the segment can be added to this cluster; otherwise, false.</returns>
    public bool CanAdd(Segment segment, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(segment);

        if (Count == 0)
        {
            return true;
        }

        var startDiff = Math.Abs(segment.Start - AverageStart);
        var endDiff = Math.Abs(segment.End - AverageEnd);

        return startDiff <= tolerance && endDiff <= tolerance;
    }

    /// <summary>
    /// Determines if a segment can be added to this cluster based on separate start and end tolerances.
    /// </summary>
    /// <param name="segment">The segment to check.</param>
    /// <param name="startTolerance">The maximum allowed difference for start times.</param>
    /// <param name="endTolerance">The maximum allowed difference for end times.</param>
    /// <returns>True if the segment can be added to this cluster; otherwise, false.</returns>
    public bool CanAdd(Segment segment, double startTolerance, double endTolerance)
    {
        ArgumentNullException.ThrowIfNull(segment);

        if (Count == 0)
        {
            return true;
        }

        var startDiff = Math.Abs(segment.Start - AverageStart);
        var endDiff = Math.Abs(segment.End - AverageEnd);

        return startDiff <= startTolerance && endDiff <= endTolerance;
    }

    /// <summary>
    /// Creates a representative segment for this cluster using average values.
    /// </summary>
    /// <param name="episodeId">The episode ID for the representative segment.</param>
    /// <returns>A segment with averaged start and end times.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the cluster is empty and no representative segment can be created.
    /// </exception>
    public Segment CreateRepresentativeSegment(Guid episodeId)
    {
        if (Count == 0)
        {
            throw new InvalidOperationException("Cannot create a representative segment from an empty cluster.");
        }

        return new Segment(episodeId, new TimeRange(AverageStart, AverageEnd));
    }
    public Segment CreateRepresentativeSegment(Guid episodeId)
    {
        return new Segment(episodeId, new TimeRange(AverageStart, AverageEnd));
    }

    /// <summary>
    /// Checks if the cluster contains a specific episode.
    /// </summary>
    /// <param name="episodeId">The episode ID to check.</param>
    /// <returns>True if the episode is in the cluster; otherwise, false.</returns>
    public bool ContainsEpisode(Guid episodeId) => _episodeIds.Contains(episodeId);

    /// <summary>
    /// Groups segments into clusters based on timing similarity.
    /// </summary>
    /// <param name="segments">The segments to cluster.</param>
    /// <param name="tolerance">The timing tolerance for clustering.</param>
    /// <returns>A list of episode clusters.</returns>
    public static IReadOnlyList<EpisodeCluster> ClusterSegments(IEnumerable<Segment> segments, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var clusters = new List<EpisodeCluster>();

        foreach (var segment in segments.Where(s => s.Valid))
        {
            var matchingCluster = clusters.FirstOrDefault(c => c.CanAdd(segment, tolerance));

            if (matchingCluster is not null)
            {
                matchingCluster.Add(segment);
            }
            else
            {
                clusters.Add(new EpisodeCluster(segment));
            }
        }

        return clusters;
    }

    /// <summary>
    /// Groups segments into clusters based on timing similarity with separate tolerances.
    /// </summary>
    /// <param name="segments">The segments to cluster.</param>
    /// <param name="startTolerance">The timing tolerance for start times.</param>
    /// <param name="endTolerance">The timing tolerance for end times.</param>
    /// <returns>A list of episode clusters.</returns>
    public static IReadOnlyList<EpisodeCluster> ClusterSegments(IEnumerable<Segment> segments, double startTolerance, double endTolerance)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var clusters = new List<EpisodeCluster>();

        foreach (var segment in segments.Where(s => s.Valid))
        {
            var matchingCluster = clusters.FirstOrDefault(c => c.CanAdd(segment, startTolerance, endTolerance));

            if (matchingCluster is not null)
            {
                matchingCluster.Add(segment);
            }
            else
            {
                clusters.Add(new EpisodeCluster(segment));
            }
        }

        return clusters;
    }
}
