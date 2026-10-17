// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using MediaBrowser.Model.Entities;
using SegmentScanner.Contracts;

#pragma warning disable SA1649 // The environment interface and its default implementations form one host seam.

namespace IntroSkipper.Data;

/// <summary>
/// Supplies analysis-only host dependencies without requiring the Jellyfin plugin singleton.
/// </summary>
internal interface IAnalysisEnvironment
{
    PluginConfiguration Configuration { get; }

    string FFmpegPath { get; }

    string? FFprobePath { get; }

    /// <summary>
    /// Gets the credits video decoder selected for this analysis job.
    /// </summary>
    CreditVideoDecoder VideoDecoder => CreditVideoDecoder.Cpu;

    IReadOnlyList<ChapterInfo> GetChapters(Guid itemId);

    string? GetCacheIdentity(Guid itemId);

    /// <summary>
    /// Gets trusted video stream metadata for hardware credits decoding.
    /// </summary>
    /// <param name="itemId">The analyzed item identifier.</param>
    /// <returns>The video metadata, or <see langword="null"/> when it is unavailable.</returns>
    CreditVideoInfo? GetCreditVideoInfo(Guid itemId) => null;
}

/// <summary>
/// Provides the trusted stream fields required to select a hardware credits decoder.
/// </summary>
/// <param name="Codec">The primary video codec name.</param>
/// <param name="PixelFormat">The primary video pixel format name.</param>
internal sealed record CreditVideoInfo(string Codec, string PixelFormat);

/// <summary>
/// Uses the active Jellyfin plugin as the server-host default analysis environment.
/// </summary>
internal sealed class PluginAnalysisEnvironment : IAnalysisEnvironment
{
    public PluginConfiguration Configuration => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public string FFmpegPath => Plugin.Instance?.FFmpegPath ?? "ffmpeg";

    public string? FFprobePath => null;

    public IReadOnlyList<ChapterInfo> GetChapters(Guid itemId) => Plugin.Instance?.GetChapters(itemId) ?? [];

    public string? GetCacheIdentity(Guid itemId) => null;
}

/// <summary>
/// Holds explicit immutable analysis inputs for a non-Jellyfin host.
/// </summary>
internal sealed class ExplicitAnalysisEnvironment(
    PluginConfiguration configuration,
    string ffmpegPath,
    Func<Guid, IReadOnlyList<ChapterInfo>> chapters,
    string? ffprobePath = null,
    Func<Guid, string?>? cacheIdentity = null,
    CreditVideoDecoder videoDecoder = CreditVideoDecoder.Cpu,
    Func<Guid, CreditVideoInfo?>? creditVideoInfo = null) : IAnalysisEnvironment
{
    public PluginConfiguration Configuration { get; } = configuration;

    public string FFmpegPath { get; } = ffmpegPath;

    public string? FFprobePath { get; } = ffprobePath;

    public CreditVideoDecoder VideoDecoder { get; } = videoDecoder;

    public IReadOnlyList<ChapterInfo> GetChapters(Guid itemId) => chapters(itemId);

    public string? GetCacheIdentity(Guid itemId) => cacheIdentity?.Invoke(itemId);

    public CreditVideoInfo? GetCreditVideoInfo(Guid itemId) => creditVideoInfo?.Invoke(itemId);
}
