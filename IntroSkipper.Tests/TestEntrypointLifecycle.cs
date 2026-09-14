// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestEntrypointLifecycle
{
    [Fact]
    public async Task StartAsync_SubscribesHandlersUntilStopAsync()
    {
        var ffmpegService = new StubFFmpegService { VersionCheck = () => true };
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { AutoDetectIntros = true });
        var (entrypoint, _, library) = EntrypointTestHelpers.CreateEntrypoint(ffmpegService);

        await entrypoint.StartAsync(CancellationToken.None);

        Assert.Equal(1, ffmpegService.VersionCheckCalls);
        Assert.Equal(1, library.ItemAddedSubscriberCount);
        Assert.Equal(1, library.ItemUpdatedSubscriberCount);
        Assert.Equal(1, library.ItemRemovedSubscriberCount);

        await entrypoint.StopAsync(CancellationToken.None);

        Assert.Equal(0, library.ItemAddedSubscriberCount);
        Assert.Equal(0, library.ItemUpdatedSubscriberCount);
        Assert.Equal(0, library.ItemRemovedSubscriberCount);
    }
}
