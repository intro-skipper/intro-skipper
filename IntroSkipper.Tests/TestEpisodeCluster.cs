// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using IntroSkipper.Data;
using Xunit;

namespace IntroSkipper.Tests;

public class TestEpisodeCluster
{
    [Fact]
    public void EmptyCluster_HasZeroCount()
    {
        var cluster = new EpisodeCluster();
        Assert.Equal(0, cluster.Count);
        Assert.Empty(cluster.EpisodeIds);
    }

    [Fact]
    public void EmptyCluster_HasZeroAverages()
    {
        var cluster = new EpisodeCluster();
        Assert.Equal(0, cluster.AverageStart);
        Assert.Equal(0, cluster.AverageEnd);
        Assert.Equal(0, cluster.AverageDuration);
    }

    [Fact]
    public void AddSegment_IncreasesCount()
    {
        var cluster = new EpisodeCluster();
        var segment = new Segment(Guid.NewGuid(), new TimeRange(10, 30));
        cluster.Add(segment);

        Assert.Equal(1, cluster.Count);
        Assert.Single(cluster.EpisodeIds);
    }

    [Fact]
    public void AddSegment_UpdatesAverages()
    {
        var cluster = new EpisodeCluster();
        var segment = new Segment(Guid.NewGuid(), new TimeRange(10, 30));
        cluster.Add(segment);

        Assert.Equal(10, cluster.AverageStart);
        Assert.Equal(30, cluster.AverageEnd);
        Assert.Equal(20, cluster.AverageDuration);
    }

    [Fact]
    public void AddMultipleSegments_CalculatesCorrectAverages()
    {
        var cluster = new EpisodeCluster();
        cluster.Add(new Segment(Guid.NewGuid(), new TimeRange(10, 30)));
        cluster.Add(new Segment(Guid.NewGuid(), new TimeRange(12, 32)));
        cluster.Add(new Segment(Guid.NewGuid(), new TimeRange(8, 28)));

        Assert.Equal(3, cluster.Count);
        Assert.Equal(10, cluster.AverageStart);
        Assert.Equal(30, cluster.AverageEnd);
        Assert.Equal(20, cluster.AverageDuration);
    }

    [Fact]
    public void ConstructorWithSegment_InitializesCorrectly()
    {
        var episodeId = Guid.NewGuid();
        var segment = new Segment(episodeId, new TimeRange(15, 45));
        var cluster = new EpisodeCluster(segment);

        Assert.Equal(1, cluster.Count);
        Assert.Contains(episodeId, cluster.EpisodeIds);
        Assert.Equal(15, cluster.AverageStart);
        Assert.Equal(45, cluster.AverageEnd);
    }

    [Fact]
    public void CanAdd_ReturnsTrueForEmptyCluster()
    {
        var cluster = new EpisodeCluster();
        var segment = new Segment(Guid.NewGuid(), new TimeRange(10, 30));

        Assert.True(cluster.CanAdd(segment, 5));
    }

    [Fact]
    public void CanAdd_ReturnsTrueWithinTolerance()
    {
        var cluster = new EpisodeCluster(new Segment(Guid.NewGuid(), new TimeRange(10, 30)));
        var segment = new Segment(Guid.NewGuid(), new TimeRange(12, 28));

        Assert.True(cluster.CanAdd(segment, 5));
    }

    [Fact]
    public void CanAdd_ReturnsFalseOutsideTolerance()
    {
        var cluster = new EpisodeCluster(new Segment(Guid.NewGuid(), new TimeRange(10, 30)));
        var segment = new Segment(Guid.NewGuid(), new TimeRange(20, 40));

        Assert.False(cluster.CanAdd(segment, 5));
    }

    [Fact]
    public void CanAdd_WithSeparateTolerances_Works()
    {
        var cluster = new EpisodeCluster(new Segment(Guid.NewGuid(), new TimeRange(10, 30)));
        var segment = new Segment(Guid.NewGuid(), new TimeRange(12, 35));

        // Start is within 5, end is within 10
        Assert.True(cluster.CanAdd(segment, 5, 10));
        Assert.False(cluster.CanAdd(segment, 5, 2));
    }

    [Fact]
    public void ContainsEpisode_ReturnsCorrectResult()
    {
        var episodeId1 = Guid.NewGuid();
        var episodeId2 = Guid.NewGuid();
        var cluster = new EpisodeCluster(new Segment(episodeId1, new TimeRange(10, 30)));

        Assert.True(cluster.ContainsEpisode(episodeId1));
        Assert.False(cluster.ContainsEpisode(episodeId2));
    }

    [Fact]
    public void CreateRepresentativeSegment_ReturnsCorrectValues()
    {
        var cluster = new EpisodeCluster();
        cluster.Add(new Segment(Guid.NewGuid(), new TimeRange(10, 30)));
        cluster.Add(new Segment(Guid.NewGuid(), new TimeRange(14, 34)));

        var representativeId = Guid.NewGuid();
        var representative = cluster.CreateRepresentativeSegment(representativeId);

        Assert.Equal(representativeId, representative.EpisodeId);
        Assert.Equal(12, representative.Start);
        Assert.Equal(32, representative.End);
    }

    [Fact]
    public void ClusterSegments_GroupsSimilarSegments()
    {
        var segments = new List<Segment>
        {
            new Segment(Guid.NewGuid(), new TimeRange(10, 30)),
            new Segment(Guid.NewGuid(), new TimeRange(12, 32)),
            new Segment(Guid.NewGuid(), new TimeRange(50, 70)),
            new Segment(Guid.NewGuid(), new TimeRange(52, 72))
        };

        var clusters = EpisodeCluster.ClusterSegments(segments, 5);

        Assert.Equal(2, clusters.Count);
        Assert.Equal(2, clusters[0].Count);
        Assert.Equal(2, clusters[1].Count);
    }

    [Fact]
    public void ClusterSegments_ExcludesInvalidSegments()
    {
        var segments = new List<Segment>
        {
            new Segment(Guid.NewGuid(), new TimeRange(10, 30)),
            new Segment(Guid.NewGuid(), new TimeRange(0, 0)), // Invalid - End is 0
            new Segment(Guid.NewGuid(), new TimeRange(12, 32))
        };

        var clusters = EpisodeCluster.ClusterSegments(segments, 5);

        Assert.Single(clusters);
        Assert.Equal(2, clusters[0].Count);
    }

    [Fact]
    public void ClusterSegments_WithSeparateTolerances_Works()
    {
        var segments = new List<Segment>
        {
            new Segment(Guid.NewGuid(), new TimeRange(10, 30)),
            new Segment(Guid.NewGuid(), new TimeRange(12, 40)),
            new Segment(Guid.NewGuid(), new TimeRange(11, 31))
        };

        // With tight end tolerance, segment 2 should be separate
        var clusters = EpisodeCluster.ClusterSegments(segments, 5, 5);

        Assert.Equal(2, clusters.Count);
    }

    [Fact]
    public void ClusterSegments_EmptyInput_ReturnsEmptyList()
    {
        var clusters = EpisodeCluster.ClusterSegments([], 5);
        Assert.Empty(clusters);
    }

    [Fact]
    public void ClusterSegments_SingleSegment_ReturnsOneCluster()
    {
        var segments = new List<Segment>
        {
            new Segment(Guid.NewGuid(), new TimeRange(10, 30))
        };

        var clusters = EpisodeCluster.ClusterSegments(segments, 5);

        Assert.Single(clusters);
        Assert.Equal(1, clusters[0].Count);
    }

    [Fact]
    public void Add_ThrowsOnNullSegment()
    {
        var cluster = new EpisodeCluster();
        Assert.Throws<ArgumentNullException>(() => cluster.Add(null!));
    }

    [Fact]
    public void CanAdd_ThrowsOnNullSegment()
    {
        var cluster = new EpisodeCluster();
        Assert.Throws<ArgumentNullException>(() => cluster.CanAdd(null!, 5));
    }

    [Fact]
    public void ClusterSegments_ThrowsOnNullInput()
    {
        Assert.Throws<ArgumentNullException>(() => EpisodeCluster.ClusterSegments(null!, 5));
    }
}
