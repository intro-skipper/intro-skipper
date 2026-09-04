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

public sealed class TestChapterAnalyzerOrchestration
{
    [Fact]
    public async Task AnalyzeMediaFiles_ReturnsUnchangedQueueWithoutPersisting_WhenChapterManagerReturnsNull()
    {
        var dbPath = CreateTempDbPath();
        var episode = CreateEpisode();
        IReadOnlyList<QueuedEpisode> queue = [episode];
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            using var pluginScope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
            ConfigurePlugin();
            var chapterManager = ChapterManagerStub.Create(null, out var chapterManagerProxy);
            EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_chapterRepository", chapterManager);
            var analyzer = new ChapterAnalyzer(NullLogger<ChapterAnalyzer>.Instance, null!, database);

            var result = await analyzer.AnalyzeMediaFiles(queue, AnalysisMode.Introduction, CancellationToken.None);

            Assert.Same(queue, result);
            Assert.Equal(1, chapterManagerProxy.GetChaptersCallCount);
            Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Introduction));
            Assert.Empty(await database.GetSegmentsAsync(episode.EpisodeId));
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task AnalyzeMediaFiles_PropagatesCancellationBeforeChapterLookupOrPersistence()
    {
        var dbPath = CreateTempDbPath();
        var episode = CreateEpisode();
        IReadOnlyList<QueuedEpisode> queue = [episode];
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            using var pluginScope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
            ConfigurePlugin();
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
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    private static void ConfigurePlugin()
    {
        EntrypointTestHelpers.SetPropertyOrField(
            Plugin.Instance!,
            "Configuration",
            new PluginConfiguration
            {
                ChapterAnalyzerIntroductionPattern = "Introduction",
            });
    }

    private static QueuedEpisode CreateEpisode() => new()
    {
        EpisodeId = Guid.NewGuid(),
        Duration = 1800,
    };

    private static string CreateTempDbPath()
        => DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-chapter-analyzer.db");
}
