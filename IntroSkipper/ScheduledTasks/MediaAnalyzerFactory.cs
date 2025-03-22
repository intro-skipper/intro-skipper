// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.ScheduledTasks;

/// <summary>
/// Manages the creation and configuration of media analyzers based on plugin settings.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="MediaAnalyzerFactory"/> class.
/// </remarks>
/// <param name="loggerFactory">Logger factory for creating analyzer-specific loggers.</param>
/// <param name="config">Plugin configuration.</param>
/// <param name="ffmpegValid">Whether FFmpeg with Chromaprint is available.</param>
public class MediaAnalyzerFactory(
    ILoggerFactory loggerFactory,
    PluginConfiguration config,
    bool ffmpegValid)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly PluginConfiguration _config = config;
    private readonly bool _ffmpegValid = ffmpegValid;

    /// <summary>
    /// Gets analyzers for all configured analysis modes.
    /// </summary>
    /// <returns>Dictionary mapping analysis modes to their associated analyzers.</returns>
    public IReadOnlyDictionary<AnalysisMode, Collection<IMediaFileAnalyzer>> GetAnalyzers()
    {
        var analyzers = new Dictionary<AnalysisMode, Collection<IMediaFileAnalyzer>>();

        // Create analyzer lists
        var introAnalyzers = ParseAnalyzers(_config.IntroAnalyzerOrderList);
        var creditsAnalyzers = ParseAnalyzers(_config.CreditsAnalyzerOrderList, includeBlackFrame: true);
        var chapterAnalyzer = new Collection<IMediaFileAnalyzer> { new ChapterAnalyzer(_loggerFactory.CreateLogger<ChapterAnalyzer>()) };

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

    /// <summary>
    /// Parses a comma-separated configuration string into a list of analyzers.
    /// </summary>
    /// <param name="configString">Configuration string from settings.</param>
    /// <param name="includeBlackFrame">Whether to include BlackFrame analyzer.</param>
    /// <returns>Collection of configured analyzers.</returns>
    private Collection<IMediaFileAnalyzer> ParseAnalyzers(string? configString, bool includeBlackFrame = false)
    {
        var result = new Collection<IMediaFileAnalyzer>();
        if (string.IsNullOrEmpty(configString))
        {
            return result;
        }

        var analyzerItems = configString.Split(',')
            .Select(item =>
            {
                var parts = item.Split(':', 2);
                if (parts.Length < 2 || !bool.TryParse(parts[1].Trim(), out var enabled))
                {
                    return (Name: parts[0].Trim(), Enabled: false);
                }

                return (Name: parts[0].Trim(), Enabled: enabled);
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

    /// <summary>
    /// Gets filtered analyzers based on specific episode and mode.
    /// </summary>
    /// <param name="first">First episode to analyze.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="analyzers">Available analyzers for this mode.</param>
    /// <returns>Collection of filtered analyzers.</returns>
    public IReadOnlyCollection<IMediaFileAnalyzer> GetFilteredAnalyzers(
        QueuedEpisode first,
        AnalysisMode mode,
        IReadOnlyCollection<IMediaFileAnalyzer> analyzers)
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
