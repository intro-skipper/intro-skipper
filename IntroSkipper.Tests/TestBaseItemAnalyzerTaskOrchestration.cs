namespace IntroSkipper.Tests;

using System;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Manager;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestBaseItemAnalyzerTaskOrchestration
{
    [Fact]
    public async Task AnalyzeItemsAsync_PropagatesCancellationToFfmpegValidation()
    {
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var analyzer = new AnalyzerTaskFactory(
            NullLoggerFactory.Instance,
            EntrypointTestHelpers.CreateLibraryManager(),
            providerManager: null!,
            fileSystem: null!,
            ffmpegService: FfmpegTestHelpers.CreateFFmpegService(),
            cacheService: null!,
            database: null!).CreateAnalyzerTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.AnalyzeItemsAsync(new Progress<double>(), cancellation.Token));
    }

    [Fact]
    public async Task AnalyzeItemsAsync_InvalidFfmpeg_IsProbedOnceAcrossAnalyzerAndQueueVerification()
    {
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var episode = JellyfinItems.Episode(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), path: "/media/missing-episode.mkv");
        var libraryManager = EntrypointTestHelpers.FakeLibraryManager.Create([JellyfinItems.Folder("Media")], [episode]);
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", libraryManager);
        var ffmpegService = new StubFFmpegService { VersionCheck = () => false };
        var analyzer = new AnalyzerTaskFactory(
            NullLoggerFactory.Instance,
            libraryManager,
            providerManager: null!,
            fileSystem: null!,
            ffmpegService,
            cacheService: null!,
            DatabaseTestHelpers.CreateTempSegmentDatabase()).CreateAnalyzerTask();

        await analyzer.AnalyzeItemsAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(1, ffmpegService.VersionCheckCalls);
    }
}
