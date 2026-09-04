namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestChapterAnalyzerOrchestration : IDisposable
{
    private readonly TempSegmentDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AnalyzeMediaFiles_ReturnsUnchangedQueueWithoutPersisting_WhenChapterManagerReturnsNull()
    {
        var episode = CreateEpisode();
        IReadOnlyList<QueuedEpisode> queue = [episode];
        var database = _db.Database;
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { ChapterAnalyzerIntroductionPattern = "Introduction" });
        var chapterManager = ChapterManagerStub.Create(null, out var chapterManagerProxy);
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_chapterRepository", chapterManager);
        var analyzer = new ChapterAnalyzer(NullLogger<ChapterAnalyzer>.Instance, null!, database);

        var result = await analyzer.AnalyzeMediaFiles(queue, AnalysisMode.Introduction, CancellationToken.None);

        Assert.Same(queue, result);
        Assert.Equal(1, chapterManagerProxy.GetChaptersCallCount);
        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Introduction));
        Assert.Empty(await database.GetSegmentsAsync(episode.EpisodeId));
    }

    [Fact]
    public async Task AnalyzeMediaFiles_PropagatesCancellationBeforeChapterLookupOrPersistence()
    {
        var episode = CreateEpisode();
        IReadOnlyList<QueuedEpisode> queue = [episode];
        var database = _db.Database;
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { ChapterAnalyzerIntroductionPattern = "Introduction" });
        var chapterManager = ChapterManagerStub.Create(null, out var chapterManagerProxy);
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_chapterRepository", chapterManager);
        var analyzer = new ChapterAnalyzer(NullLogger<ChapterAnalyzer>.Instance, null!, database);
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.AnalyzeMediaFiles(queue, AnalysisMode.Introduction, cancellationSource.Token));

        Assert.Equal(0, chapterManagerProxy.GetChaptersCallCount);
        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Introduction));
        Assert.Empty(await database.GetSegmentsAsync(episode.EpisodeId));
    }

    private static QueuedEpisode CreateEpisode() => new()
    {
        EpisodeId = Guid.NewGuid(),
        Duration = 1800,
    };
}
