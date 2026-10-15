// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using MediaBrowser.Model.Entities;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Selects the chapter source used by analysis. The default remains Jellyfin's chapter
/// repository for speed; the optional media-side source avoids chapters injected by plugins.
/// </summary>
internal static class AnalysisChapterProvider
{
    /// <summary>
    /// Gets the configured analysis chapters for an episode.
    /// </summary>
    /// <param name="config">Active plugin configuration.</param>
    /// <param name="ffmpegService">FFmpeg service used for the optional embedded probe.</param>
    /// <param name="episode">Episode whose chapters are needed.</param>
    /// <param name="cancellationToken">Token used to cancel the caller's wait for the shared embedded probe;
    /// an item without a Jellyfin file version is probed uncached and cancels its process too.</param>
    /// <returns>The chapters selected by configuration, or an empty list when the source has none.</returns>
    /// <exception cref="InvalidOperationException">The embedded chapter probe failed.</exception>
    public static Task<IReadOnlyList<ChapterInfo>> GetAsync(
        PluginConfiguration config,
        IFFmpegService ffmpegService,
        QueuedEpisode episode,
        CancellationToken cancellationToken)
    {
        if (!config.UseEmbeddedChaptersForAnalysis)
        {
            return Task.FromResult<IReadOnlyList<ChapterInfo>>(Plugin.Instance?.GetChapters(episode.EpisodeId) ?? []);
        }

        return GetEmbeddedAsync(ffmpegService, episode, cancellationToken);
    }

    private static async Task<IReadOnlyList<ChapterInfo>> GetEmbeddedAsync(
        IFFmpegService ffmpegService,
        QueuedEpisode episode,
        CancellationToken cancellationToken)
        => await ffmpegService.GetEmbeddedChaptersAsync(episode, cancellationToken).ConfigureAwait(false);
}
