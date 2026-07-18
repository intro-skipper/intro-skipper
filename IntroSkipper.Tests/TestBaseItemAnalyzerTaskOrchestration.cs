namespace IntroSkipper.Tests;

using System;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.FFmpeg;
using IntroSkipper.ScheduledTasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestBaseItemAnalyzerTaskOrchestration
{
    [Fact]
    public async Task AnalyzeItemsAsync_PropagatesCancellationToFfmpegValidation()
    {
        using var pluginScope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var analyzer = new BaseItemAnalyzerTask(
            NullLogger.Instance,
            NullLoggerFactory.Instance,
            EntrypointTestHelpers.CreateLibraryManager(),
            providerManager: null!,
            fileSystem: null!,
            mediaSegmentRefresher: null!,
            ffmpegService: new FFmpegService(
                NullLogger<FFmpegService>.Instance,
                DatabaseTestHelpers.CreateTempCacheService()),
            cacheService: null!,
            database: null!);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.AnalyzeItemsAsync(new Progress<double>(), cancellation.Token));
    }
}
