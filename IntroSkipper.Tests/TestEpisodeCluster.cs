// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using IntroSkipper.Data;
using Xunit;

namespace IntroSkipper.Tests;

public class TestEpisodeCluster
{
    [Fact]
    public void Find_ReturnsEpisodeId_WhenNotRegistered()
    {
        var cluster = new EpisodeCluster();
        var episodeId = Guid.NewGuid();

        var result = cluster.Find(episodeId);

        Assert.Equal(episodeId, result);
    }

    [Fact]
    public void Find_ReturnsSelf_WhenRegistered()
    {
        var cluster = new EpisodeCluster();
        var episodeId = Guid.NewGuid();
        cluster.Register(episodeId, 1);

        var result = cluster.Find(episodeId);

        Assert.Equal(episodeId, result);
    }

    [Fact]
    public void IsFirstAppearance_ReturnsTrue_WhenEpisodeIsItsOwnRoot()
    {
        var cluster = new EpisodeCluster();
        var episodeId = Guid.NewGuid();
        cluster.Register(episodeId, 1);

        var result = cluster.IsFirstAppearance(episodeId);

        Assert.True(result);
    }

    [Fact]
    public void Union_MakesLowerEpisodeNumberTheRoot()
    {
        var cluster = new EpisodeCluster();
        var episode1 = Guid.NewGuid();
        var episode5 = Guid.NewGuid();

        cluster.Register(episode1, 1);
        cluster.Register(episode5, 5);

        cluster.Union(episode5, episode1);

        // Episode 1 has lower episode number, so it should be the root
        Assert.Equal(episode1, cluster.Find(episode5));
        Assert.Equal(episode1, cluster.Find(episode1));
        Assert.True(cluster.IsFirstAppearance(episode1));
        Assert.False(cluster.IsFirstAppearance(episode5));
    }

    [Fact]
    public void Union_OrderDoesNotMatter()
    {
        var cluster = new EpisodeCluster();
        var episode3 = Guid.NewGuid();
        var episode7 = Guid.NewGuid();

        cluster.Register(episode3, 3);
        cluster.Register(episode7, 7);

        // Union with episode7 first
        cluster.Union(episode7, episode3);

        // Episode 3 has lower episode number, so it should be the root regardless of order
        Assert.Equal(episode3, cluster.Find(episode7));
        Assert.True(cluster.IsFirstAppearance(episode3));
        Assert.False(cluster.IsFirstAppearance(episode7));
    }

    [Fact]
    public void Union_TransitivityWorks()
    {
        var cluster = new EpisodeCluster();
        var episode2 = Guid.NewGuid();
        var episode4 = Guid.NewGuid();
        var episode6 = Guid.NewGuid();

        cluster.Register(episode2, 2);
        cluster.Register(episode4, 4);
        cluster.Register(episode6, 6);

        // Union 4 and 6 first
        cluster.Union(episode4, episode6);

        // Then union 2 and 4
        cluster.Union(episode2, episode4);

        // Episode 2 should be the root of the entire cluster
        Assert.Equal(episode2, cluster.Find(episode2));
        Assert.Equal(episode2, cluster.Find(episode4));
        Assert.Equal(episode2, cluster.Find(episode6));
        Assert.True(cluster.IsFirstAppearance(episode2));
        Assert.False(cluster.IsFirstAppearance(episode4));
        Assert.False(cluster.IsFirstAppearance(episode6));
    }

    [Fact]
    public void Union_SameEpisodeDoesNothing()
    {
        var cluster = new EpisodeCluster();
        var episodeId = Guid.NewGuid();
        cluster.Register(episodeId, 1);

        cluster.Union(episodeId, episodeId);

        Assert.True(cluster.IsFirstAppearance(episodeId));
    }

    [Fact]
    public void GetFirstAppearance_ReturnsRootOfCluster()
    {
        var cluster = new EpisodeCluster();
        var episode1 = Guid.NewGuid();
        var episode3 = Guid.NewGuid();
        var episode5 = Guid.NewGuid();

        cluster.Register(episode1, 1);
        cluster.Register(episode3, 3);
        cluster.Register(episode5, 5);

        cluster.Union(episode3, episode5);
        cluster.Union(episode1, episode3);

        // Episode 1 should be the first appearance for all episodes in the cluster
        Assert.Equal(episode1, cluster.GetFirstAppearance(episode1));
        Assert.Equal(episode1, cluster.GetFirstAppearance(episode3));
        Assert.Equal(episode1, cluster.GetFirstAppearance(episode5));
    }

    [Fact]
    public void MultipleClusters_RemainSeparate()
    {
        var cluster = new EpisodeCluster();
        var episodeA1 = Guid.NewGuid();
        var episodeA2 = Guid.NewGuid();
        var episodeB1 = Guid.NewGuid();
        var episodeB2 = Guid.NewGuid();

        cluster.Register(episodeA1, 1);
        cluster.Register(episodeA2, 2);
        cluster.Register(episodeB1, 10);
        cluster.Register(episodeB2, 20);

        cluster.Union(episodeA1, episodeA2);
        cluster.Union(episodeB1, episodeB2);

        // Cluster A
        Assert.Equal(episodeA1, cluster.Find(episodeA1));
        Assert.Equal(episodeA1, cluster.Find(episodeA2));

        // Cluster B
        Assert.Equal(episodeB1, cluster.Find(episodeB1));
        Assert.Equal(episodeB1, cluster.Find(episodeB2));

        // Clusters are separate
        Assert.NotEqual(cluster.Find(episodeA1), cluster.Find(episodeB1));
    }

    [Fact]
    public void Register_IsIdempotent()
    {
        var cluster = new EpisodeCluster();
        var episodeId = Guid.NewGuid();

        cluster.Register(episodeId, 5);
        cluster.Register(episodeId, 10); // Should be ignored

        // Original registration should be preserved
        Assert.True(cluster.IsFirstAppearance(episodeId));
    }
}
