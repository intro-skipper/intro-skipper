// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntroSkipper.Tests;

/// <summary>
/// Shared factory methods for creating FFmpeg service instances in tests.
/// </summary>
internal static class TestServiceFactory
{
    internal static IFFmpegCapabilityService CreateCapabilityService()
    {
        var optionsProvider = new PluginOptionsProvider();
        var runner = new FFmpegRunner(optionsProvider, NullLogger<FFmpegRunner>.Instance);
        return new FFmpegCapabilityService(runner, NullLogger<FFmpegCapabilityService>.Instance);
    }

    internal static IMediaDetectionService CreateDetectionService()
    {
        var optionsProvider = new PluginOptionsProvider();
        var runner = new FFmpegRunner(optionsProvider, NullLogger<FFmpegRunner>.Instance);
        var cacheService = new DetectionCacheService(optionsProvider, NullLogger<DetectionCacheService>.Instance);
        return new MediaDetectionService(runner, cacheService, optionsProvider, NullLogger<MediaDetectionService>.Instance);
    }

    internal static DetectionCacheService CreateCacheService()
    {
        var optionsProvider = new PluginOptionsProvider();
        return new DetectionCacheService(optionsProvider, NullLogger<DetectionCacheService>.Instance);
    }
}
