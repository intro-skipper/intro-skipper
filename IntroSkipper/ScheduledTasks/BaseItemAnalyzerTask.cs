// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.ScheduledTasks;

/// <summary>
/// Common code shared by all media item analyzer tasks.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="BaseItemAnalyzerTask"/> class.
/// </remarks>
/// <param name="logger">Task logger.</param>
/// <param name="loggerFactory">Logger factory.</param>
/// <param name="libraryManager">Library manager.</param>
/// <param name="mediaSegmentUpdateManager">Media segment update manager.</param>
public class BaseItemAnalyzerTask(
    ILogger logger,
    ILoggerFactory loggerFactory,
    ILibraryManager libraryManager,
    MediaSegmentUpdateManager mediaSegmentUpdateManager)
{
    private readonly ILogger _logger = logger;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly MediaSegmentUpdateManager _mediaSegmentUpdateManager = mediaSegmentUpdateManager;
    private readonly PluginConfiguration _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private readonly bool _ffmpegValid = FFmpegWrapper.CheckFFmpegVersion();
    private Dictionary<AnalysisMode, List<IMediaFileAnalyzer>> _analyzers = [];

    /// <summary>
    /// Analyze all media items on the server.
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="seasonsToAnalyze">Season IDs to analyze.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AnalyzeItemsAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? seasonsToAnalyze = null)
    {
        var queueManager = new QueueManager(
            _loggerFactory.CreateLogger<QueueManager>(),
            _libraryManager);

        var queue = queueManager.GetMediaItems();

        if (seasonsToAnalyze?.Count > 0)
        {
            queue = queue.Where(kvp => seasonsToAnalyze.Contains(kvp.Key))
                         .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        if (!_ffmpegValid)
        {
            _logger.LogInformation(
                "Skipping Chromaprint analysis! Chromaprint is not enabled in the current ffmpeg. " +
                "If Jellyfin is running natively, install jellyfin-ffmpeg7. " +
                "If Jellyfin is running in a container, upgrade to version 10.10.0 or newer.");
        }

        _analyzers = GetAnalyzers();

        var modes = _analyzers.Keys;

        int totalQueued = queue.Sum(kvp => kvp.Value.Count) * modes.Count;
        if (totalQueued == 0)
        {
            _logger.LogInformation("No libraries selected for analysis. To enable, check library configuration > Media Segment Providers.");
            return;
        }

        int totalProcessed = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _config.MaxParallelism),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(queue, options, async (season, ct) =>
        {
            var updateMediaSegments = false;

            var episodes = queueManager.VerifyQueue(season.Value, modes);
            if (episodes.Count == 0)
            {
                return;
            }

            try
            {
                var firstEpisode = episodes[0];

                foreach (var mode in modes)
                {
                    ct.ThrowIfCancellationRequested();
                    int analyzed = await AnalyzeItemsAsync(
                        episodes,
                        mode,
                        ct).ConfigureAwait(false);
                    Interlocked.Add(ref totalProcessed, episodes.Count);

                    updateMediaSegments = analyzed > 0 || updateMediaSegments;
                    progress.Report((double)totalProcessed / totalQueued * 100);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Analysis was canceled.");
            }
            catch (FingerprintException ex)
            {
                _logger.LogWarning(ex, "Fingerprint exception during analysis.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred during analysis.");
                throw;
            }

            if (_config.RebuildMediaSegments || (updateMediaSegments && _config.UpdateMediaSegments))
            {
                await _mediaSegmentUpdateManager.UpdateMediaSegmentsAsync(episodes, ct).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        Plugin.Instance!.AnalyzeAgain = false;

        if (_config.RebuildMediaSegments)
        {
            _logger.LogInformation("Regenerated media segments.");
            _config.RebuildMediaSegments = false;
            Plugin.Instance!.SaveConfiguration();
        }
    }

    /// <summary>
    /// Analyze a group of media items for skippable segments.
    /// </summary>
    /// <param name="items">Media items to analyze.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of items successfully analyzed.</returns>
    private async Task<int> AnalyzeItemsAsync(
        IReadOnlyList<QueuedEpisode> items,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        if (!items.Any(e => e.GetAnalyzed(mode) == EpisodeState.NotAnalyzed))
        {
            return 0;
        }

        var first = items[0];
        if (!first.IsMovie && first.SeasonNumber == 0 && !_config.AnalyzeSeasonZero)
        {
            return 0;
        }

        var totalItems = items.Count(e => e.GetAnalyzed(mode) != EpisodeState.Analyzed);

        _logger.LogInformation(
            "[Mode: {Mode}] Analyzing {Count} files from {Name} season {Season}",
            mode,
            items.Count,
            first.SeriesName,
            first.SeasonNumber);

        var analyzers = GetFilteredAnalyzers(first, mode, _analyzers[mode]);

        // Use each analyzer to find skippable ranges in all media files, removing successfully
        // analyzed items from the queue.
        foreach (var analyzer in analyzers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items = await analyzer.AnalyzeMediaFiles(items, mode, cancellationToken).ConfigureAwait(false);
        }

        // Set the episode IDs for the analyzed items
        await Plugin.Instance!.SetEpisodeIdsAsync(first.SeasonId, mode, items.Select(i => i.EpisodeId)).ConfigureAwait(false);

        return totalItems - items.Count(e => e.GetAnalyzed(mode) != EpisodeState.Analyzed);
    }

    private Dictionary<AnalysisMode, List<IMediaFileAnalyzer>> GetAnalyzers()
    {
        var analyzers = new Dictionary<AnalysisMode, List<IMediaFileAnalyzer>>();

        // Create analyzer lists
        var introAnalyzers = ParseAnalyzers(_config.IntroAnalyzerOrderList);
        var creditsAnalyzers = ParseAnalyzers(_config.CreditsAnalyzerOrderList, includeBlackFrame: true);
        var chapterAnalyzer = new List<IMediaFileAnalyzer> { new ChapterAnalyzer(_loggerFactory.CreateLogger<ChapterAnalyzer>()) };

        // Populate the dictionary
        if (introAnalyzers.Count > 0)
        {
            analyzers.Add(AnalysisMode.Introduction, introAnalyzers);
        }

        if (creditsAnalyzers.Count > 0)
        {
            analyzers.Add(AnalysisMode.Credits, creditsAnalyzers);
        }

        if (_config.ScanRecap)
        {
            analyzers.Add(AnalysisMode.Recap, chapterAnalyzer);
        }

        if (_config.ScanPreview)
        {
            analyzers.Add(AnalysisMode.Preview, chapterAnalyzer);
        }

        return analyzers;
    }

    private List<IMediaFileAnalyzer> ParseAnalyzers(string? configString, bool includeBlackFrame = false)
    {
        var result = new List<IMediaFileAnalyzer>();
        if (string.IsNullOrEmpty(configString))
        {
            return result;
        }

        var analyzerItems = configString.Split(',')
            .Select(item =>
            {
                var parts = item.Split(':', 2);
                return (Name: parts[0].Trim(), Enabled: bool.Parse(parts[1]));
            })
            .Where(analyzer => analyzer.Enabled)
            .ToList();

        foreach (var (name, _) in analyzerItems)
        {
            IMediaFileAnalyzer? instance = name switch
            {
                "Chapter" => new ChapterAnalyzer(_loggerFactory.CreateLogger<ChapterAnalyzer>()),
                "Chromaprint" when _ffmpegValid => new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>()),
                "BlackFrame" when includeBlackFrame => new BlackFrameAnalyzer(_loggerFactory.CreateLogger<BlackFrameAnalyzer>()),
                _ => null
            };

            if (instance is not null)
            {
                result.Add(instance);
            }
        }

        return result;
    }

    private List<IMediaFileAnalyzer> GetFilteredAnalyzers(QueuedEpisode first, AnalysisMode mode, List<IMediaFileAnalyzer> analyzers)
    {
        List<IMediaFileAnalyzer> result = Plugin.Instance!.GetAnalyzerAction(first.SeasonId, mode) switch
        {
            AnalyzerAction.Chapter => [.. analyzers.OfType<ChapterAnalyzer>()],
            AnalyzerAction.Chromaprint => [.. analyzers.OfType<ChromaprintAnalyzer>()],
            AnalyzerAction.BlackFrame => [.. analyzers.OfType<BlackFrameAnalyzer>()],
            AnalyzerAction.None => [],
            _ when first.IsMovie => [.. analyzers.Where(a => a is not ChromaprintAnalyzer)],
            _ => [.. analyzers]
        };

        if (result.Count > 1 && first.IsAnime && _config.AnimeChromaprint && mode == AnalysisMode.Credits)
        {
            int chromaprintIndex = result.FindIndex(a => a is ChromaprintAnalyzer);
            int blackFrameIndex = result.FindIndex(a => a is BlackFrameAnalyzer);

            // Swap the analyzers if needed
            if (chromaprintIndex != -1 && blackFrameIndex != -1 && chromaprintIndex > blackFrameIndex)
            {
                (result[blackFrameIndex], result[chromaprintIndex]) = (result[chromaprintIndex], result[blackFrameIndex]);
            }
        }

        return result;
    }
}
