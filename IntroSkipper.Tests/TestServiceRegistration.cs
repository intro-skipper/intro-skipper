// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.FFmpeg;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntroSkipper.Tests;

public class TestServiceRegistration
{
    [Fact]
    public void RegisterServices_ResolvesFfmpegServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        new PluginServiceRegistrator().RegisterServices(services, applicationHost: null!);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IPluginOptionsProvider>());
        Assert.NotNull(provider.GetRequiredService<IFFmpegRunner>());
        Assert.NotNull(provider.GetRequiredService<IDetectionCacheService>());
        Assert.NotNull(provider.GetRequiredService<IDetectionResultCache>());
        Assert.NotNull(provider.GetRequiredService<IMediaDetectionService>());
        Assert.NotNull(provider.GetRequiredService<IFFmpegCapabilityService>());
    }
}
