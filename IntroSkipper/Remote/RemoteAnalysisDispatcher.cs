// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using MediaBrowser.Controller;
using SegmentScanner.Contracts;
using PluginMode = IntroSkipper.Data.AnalysisMode;
using RemoteMode = SegmentScanner.Contracts.RemoteAnalysisMode;

namespace IntroSkipper.Remote;

/// <summary>
/// Builds immutable server snapshots and persists remote work before local ownership changes.
/// </summary>
public sealed class RemoteAnalysisDispatcher(IntroSkipperDatabase database, IServerApplicationHost applicationHost) : IRemoteAnalysisDispatcher
{
    private readonly IntroSkipperDatabase _database = database;
    private readonly IServerApplicationHost _applicationHost = applicationHost;

    /// <inheritdoc />
    public async Task<IReadOnlySet<Guid>> QueueAsync(
        IReadOnlyList<QueuedEpisode> writeTargets,
        IReadOnlyList<QueuedEpisode> comparisonReferences,
        PluginMode mode,
        CancellationToken token,
        AnalyzerAction action = AnalyzerAction.Default)
    {
        if (writeTargets.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null)
        {
            return new HashSet<Guid>();
        }

        var remoteMode = ToRemoteMode(mode);
        var nodes = await _database.GetEnabledRemoteNodesAsync(token).ConfigureAwait(false);
        var assignments = writeTargets
            .Select(target => new { Target = target, Node = nodes.FirstOrDefault(node => NodeOwns(node, target, remoteMode)) })
            .Where(assignment => assignment.Node is not null)
            .GroupBy(assignment => assignment.Node!.NodeId, StringComparer.Ordinal)
            .ToList();
        var remotelyOwned = new HashSet<Guid>();
        foreach (var assignment in assignments)
        {
            var node = assignment.First().Node!;
            var selected = assignment.Select(value => value.Target).ToArray();
            var recipe = node.Runtime?.CreditsRecipes.FirstOrDefault(recipe => recipe.Method == node.CreditsMethod && recipe.UseGpu == (node.Policy?.UseGpu ?? false));
            if (mode == PluginMode.Credits && node.Policy is not null && recipe is null)
            {
                continue;
            }

            var selectedIds = selected.Select(item => item.EpisodeId).ToHashSet();
            var needsReferences = mode is PluginMode.Introduction or PluginMode.Recap || (mode == PluginMode.Credits && node.CreditsMethod == CreditsDetectionMethod.IntroSkipper);
            var allItems = (needsReferences ? comparisonReferences.Concat(selected).Where(item => NodeOwns(node, item, remoteMode)) : selected)
                .DistinctBy(item => item.EpisodeId)
                .OrderBy(item => item.EpisodeNumber)
                .ToArray();
            if (allItems.Length is 0 or > 100)
            {
                continue;
            }

            var settings = GetIntroductionSettings(configuration, selected[0].AnalysisPercentOverride, selected[0].AnalysisLengthLimitOverride, action);
            var expected = new Dictionary<Guid, Dictionary<RemoteMode, string>>();
            var contexts = new Dictionary<Guid, List<ContextSegment>>();
            var contextHashes = new Dictionary<Guid, Dictionary<RemoteMode, string>>();
            foreach (var item in allItems)
            {
                var revisions = await _database.GetRemoteRevisionsAsync(item.EpisodeId, token).ConfigureAwait(false);
                expected[item.EpisodeId] = new Dictionary<RemoteMode, string>
                {
                    [remoteMode] = revisions.TryGetValue(remoteMode, out var revision) ? revision.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0",
                };
                if (mode is PluginMode.Recap or PluginMode.Preview)
                {
                    var stored = await _database.GetSegmentsAsync(item.EpisodeId, cancellationToken: token).ConfigureAwait(false);
                    var segments = stored.Where(segment => segment.State == SegmentState.Active && segment.Type is PluginMode.Introduction or PluginMode.Credits)
                        .OrderBy(segment => segment.StartTicks).ThenBy(segment => segment.EndTicks)
                        .Select(segment => new ContextSegment(ToRemoteMode(segment.Type), segment.StartTicks, segment.EndTicks)).ToList();
                    contexts[item.EpisodeId] = segments;
                    contextHashes[item.EpisodeId] = new[] { RemoteMode.Introduction, RemoteMode.Credits }
                        .ToDictionary(contextMode => contextMode, contextMode => WireJson.Hash(segments.Where(segment => segment.Mode == contextMode).ToList()));
                }
            }

            var snapshot = new JobSnapshot
            {
                JobId = Guid.CreateVersion7(),
                AttemptId = Guid.CreateVersion7(),
                ServerId = _applicationHost.SystemId,
                NodeId = node.NodeId,
                NodeRevision = node.Revision,
                Modes = [remoteMode],
                Items = allItems.Select((item, order) => new JobItem
                {
                    ItemId = item.EpisodeId,
                    MediaSourceKey = item.Path,
                    SourcePath = item.Path,
                    FileVersionToken = (item.FileVersion ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    SubtitleFileVersionToken = configuration.ActiveSubtitlePattern(item, mode) is null
                        ? null : (item.SubtitleFileVersion ?? item.FileVersion ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ServerAnalysisConfigHash = item.AnalysisConfigHash,
                    ExpectedModeRevisions = expected[item.EpisodeId],
                    SeasonKey = item.SeasonId.ToString("D"),
                    Order = order,
                    WriteTarget = selectedIds.Contains(item.EpisodeId),
                    RuntimeTicks = checked((long)(item.Duration * TimeSpan.TicksPerSecond)),
                    Chapters = GetChapterSnapshots(item),
                    ChaptersAreAuthoritative = true,
                    IsMovie = item.Category == QueuedMediaCategory.Movie,
                    IsAnime = item.Category == QueuedMediaCategory.AnimeEpisode,
                    SeasonNumber = item.SeasonNumber,
                    EpisodeNumber = item.EpisodeNumber,
                    ContextSegments = contexts.GetValueOrDefault(item.EpisodeId),
                    ContextRevisions = contextHashes.GetValueOrDefault(item.EpisodeId),
                }).ToList(),
                IntroductionSettings = settings,
                IntroductionProfileHash = WireJson.Hash(settings),
                CreditsProfileHash = mode == PluginMode.Credits ? node.CreditsProfileHash : string.Empty,
                CreditsMethod = mode == PluginMode.Credits ? node.CreditsMethod : CreditsDetectionMethod.TextAware,
                CreditsSettings = mode == PluginMode.Credits && node.CreditsMethod == CreditsDetectionMethod.IntroSkipper
                    ? GetCreditsSettings(configuration, action) : null,
                ScanSettings = GetScanSettings(configuration, selected[0], action),
                NodePolicy = node.Policy,
                CreditsRecipe = mode == PluginMode.Credits ? recipe : null,
            };
            var reviewHeldIds = await _database.GetRemoteReviewHeldItemIdsAsync(snapshot, token).ConfigureAwait(false);
            var queuedIds = selectedIds.Except(reviewHeldIds).ToHashSet();
            if (queuedIds.Count > 0)
            {
                snapshot = snapshot with { Items = snapshot.Items.Select(item => item with { WriteTarget = queuedIds.Contains(item.ItemId) }).ToList() };
                if (await _database.QueueRemoteJobAsync(snapshot, token).ConfigureAwait(false))
                {
                    remotelyOwned.UnionWith(selectedIds);
                }
            }
            else if (reviewHeldIds.Count == selectedIds.Count
                && await _database.IsCurrentRemoteNodeAsync(node.NodeId, node.Revision, token).ConfigureAwait(false))
            {
                remotelyOwned.UnionWith(selectedIds);
            }
        }

        return remotelyOwned;
    }

    internal static bool NodeOwns(RemoteNodeInfo node, QueuedEpisode item, RemoteMode mode)
    {
        var owner = Plugin.Instance?.GetItem(item.SeriesId == Guid.Empty ? item.EpisodeId : item.SeriesId);
        return !node.IsLocal && node.Modes.Contains(mode)
            && (node.Runtime is null || node.Runtime.SupportedModes.Contains(mode))
            && owner is not null
            && Plugin.Instance?.IsInLibrary(owner, node.LibraryIds) == true
            && node.ServerPaths.Any(path => path.Matches(item.Path));
    }

    private static RemoteMode ToRemoteMode(PluginMode mode)
        => RemoteModes.ToRemote(mode);

    internal static string GetIntroductionProfileHash(
        PluginConfiguration configuration,
        int? analysisPercent = null,
        int? length = null,
        AnalyzerAction action = AnalyzerAction.Default)
        => WireJson.Hash(GetIntroductionSettings(configuration, analysisPercent, length, action));

    /// <summary>
    /// Gets a canonical hash for the immutable chapter input supplied with a job item.
    /// </summary>
    /// <param name="chapters">Chapter snapshots supplied to the worker.</param>
    /// <returns>The canonical chapter-input hash.</returns>
    internal static string GetChapterInputHash(IReadOnlyList<ChapterSnapshot> chapters)
        => WireJson.Hash(chapters
            .OrderBy(chapter => chapter.StartTicks)
            .ThenBy(chapter => chapter.Name, StringComparer.Ordinal)
            .ThenBy(chapter => chapter.Origin)
            .ThenBy(chapter => chapter.OriginId, StringComparer.Ordinal)
            .ThenBy(chapter => chapter.ExportRevision, StringComparer.Ordinal)
            .ToList());

    /// <summary>
    /// Determines whether unknown chapter provenance prevents automatic introduction acceptance.
    /// </summary>
    /// <param name="result">Introduction proposal.</param>
    /// <param name="item">Immutable item inputs.</param>
    /// <param name="settings">Effective introduction settings.</param>
    /// <returns>Whether chapter-dependent output requires review.</returns>
    internal static bool HasUnknownChapterDependency(AnalysisResult result, JobItem item, JsonElement settings)
    {
        if (result.Mode != RemoteMode.Introduction || !item.Chapters.Any(chapter => chapter.Origin == ChapterOrigin.Unknown))
        {
            return false;
        }

        var adjustsToChapters = settings.TryGetProperty("adjustIntroBasedOnChapters", out var adjusts) && adjusts.ValueKind == JsonValueKind.True;
        var prefersFingerprint = settings.TryGetProperty("preferChromaprint", out var prefers) && prefers.ValueKind == JsonValueKind.True;
        var matchesChapters = (settings.TryGetProperty("chapterAnalyzerIntroductionPattern", out var pattern) && !string.IsNullOrWhiteSpace(pattern.GetString()))
            || (settings.TryGetProperty("enableSponsorBlockChapterDetection", out var sponsorBlock) && sponsorBlock.ValueKind == JsonValueKind.True);
        return result.Segments.Count == 0 || result.Detector.Id == "upstream-chapters" || adjustsToChapters || (!prefersFingerprint && matchesChapters);
    }

    /// <summary>
    /// Snapshots the server's chapter input without inferring authorship from Jellyfin metadata.
    /// </summary>
    /// <param name="itemId">Library item whose current Jellyfin chapter table is requested.</param>
    /// <returns>Ordered chapter snapshots with unknown table provenance.</returns>
    internal static List<ChapterSnapshot> GetChapterSnapshots(Guid itemId)
        => GetChapterSnapshots(new QueuedEpisode { Chapters = Plugin.Instance?.GetChapters(itemId) ?? [] });

    /// <summary>
    /// Exports the chapter source already read by the season pass, without probing again.
    /// </summary>
    /// <param name="episode">Episode with the selected source's immutable chapters.</param>
    /// <returns>Ordered chapter snapshots; embedded chapters have known container provenance.</returns>
    internal static List<ChapterSnapshot> GetChapterSnapshots(QueuedEpisode episode)
        => episode.Chapters
            .Where(chapter => chapter.StartPositionTicks >= 0)
            .OrderBy(chapter => chapter.StartPositionTicks)
            .ThenBy(chapter => chapter.Name, StringComparer.Ordinal)
            .Select(chapter => new ChapterSnapshot
            {
                Name = chapter.Name ?? string.Empty,
                StartTicks = chapter.StartPositionTicks,
                Origin = episode.ChaptersSource == ChapterSource.Embedded ? ChapterOrigin.Authored : ChapterOrigin.Unknown,
            })
            .ToList();

    /// <summary>
    /// Builds the portable introduction profile shared with the worker.
    /// </summary>
    /// <param name="configuration">Current plugin configuration.</param>
    /// <param name="analysisPercent">Optional season analysis-percent override.</param>
    /// <param name="length">Optional season analysis-length override.</param>
    /// <param name="action">Effective season analyzer action.</param>
    /// <returns>The reduced portable profile.</returns>
    internal static JsonElement GetIntroductionSettings(
        PluginConfiguration configuration,
        int? analysisPercent = null,
        int? length = null,
        AnalyzerAction action = AnalyzerAction.Default)
    {
        using var document = JsonDocument.Parse(WireJson.Write(IntroductionProfile.From(configuration, analysisPercent, length, action)));
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Captures the original credits detector's effective inputs without unrelated server settings.
    /// </summary>
    /// <param name="configuration">Effective plugin configuration.</param>
    /// <param name="action">Effective season analyzer action.</param>
    /// <returns>Immutable portable CPU credits settings.</returns>
    internal static JsonElement GetCreditsSettings(PluginConfiguration configuration, AnalyzerAction action = AnalyzerAction.Default)
        => JsonSerializer.SerializeToElement(
            new
            {
            Action = action.ToString(),
            Configuration = new
            {
                configuration.MinimumIntroDuration,
                configuration.MinimumCreditsDuration,
                configuration.MaximumCreditsDuration,
                configuration.MaximumMovieCreditsDuration,
                configuration.ProbeAudioDuration,
                configuration.PreferredAudioLanguage,
                configuration.PreferAudioStreamWithMostChannels,
                configuration.ChapterAnalyzerEndCreditsPattern,
                configuration.FullLengthChapters,
                configuration.EnableSponsorBlockChapterDetection,
                configuration.EnhanceChapterCredits,
                configuration.BlackFrameMinimumPercentage,
                configuration.BlackFrameThreshold,
                configuration.UseLegacyBlackFrameAnalyzer,
                configuration.UseChapterMarkersBlackFrame,
                configuration.CacheCompressionLevel,
                configuration.ProcessThreads,
                configuration.ScanTimeoutSeconds,
                configuration.DetectNonBlackCredits,
                configuration.RefineCreditsBoundary,
                configuration.MaximumFingerprintPointDifferences,
                configuration.MaximumTimeSkip,
                configuration.InvertedIndexShift,
                configuration.AdjustIntroBasedOnChapters,
                configuration.AdjustIntroBasedOnSilence,
                configuration.SnapToKeyframe,
                configuration.EndSnapThreshold,
                configuration.AdjustWindowInward,
                configuration.AdjustWindowOutward,
                configuration.IntroEndOffset,
                configuration.IntroStartOffset,
                configuration.IncludeIntroStartOffsetWhenSnapping,
                configuration.SilenceDetectionMaximumNoise,
                configuration.SilenceDetectionMinimumDuration,
            },
            },
            WireJson.Options);

    /// <summary>
    /// Captures the shared built-in analyzer configuration for a portable job.
    /// </summary>
    /// <param name="configuration">Effective server configuration.</param>
    /// <param name="episode">Optional effective season overrides.</param>
    /// <param name="action">Effective analyzer action.</param>
    /// <returns>The immutable shared scan settings.</returns>
    internal static JobScanSettings GetScanSettings(PluginConfiguration configuration, QueuedEpisode? episode = null, AnalyzerAction action = AnalyzerAction.Default)
        => SharedScanSettings.Capture(configuration, episode, action);

    internal sealed record IntroductionProfile(
        int MinimumIntroDuration,
        int MaximumIntroDuration,
        int AnalysisPercent,
        int AnalysisLengthLimit,
        string PreferredAudioLanguage,
        bool PreferAudioStreamWithMostChannels,
        string ChapterAnalyzerIntroductionPattern,
        bool FullLengthChapters,
        bool EnableSponsorBlockChapterDetection,
        bool PreferChromaprint,
        bool AdjustIntroBasedOnChapters,
        bool AdjustIntroBasedOnSilence,
        bool SnapToKeyframe,
        double EndSnapThreshold,
        double AdjustWindowInward,
        double AdjustWindowOutward,
        int IntroEndOffset,
        int IntroStartOffset,
        bool IncludeIntroStartOffsetWhenSnapping,
        int MaximumFingerprintPointDifferences,
        double MaximumTimeSkip,
        int InvertedIndexShift,
        int SilenceDetectionMaximumNoise,
        double SilenceDetectionMinimumDuration)
    {
        public static IntroductionProfile From(PluginConfiguration configuration, int? analysisPercentOverride, int? analysisLengthLimitOverride, AnalyzerAction action)
            => new(
                configuration.MinimumIntroDuration,
                configuration.MaximumIntroDuration,
                analysisPercentOverride ?? configuration.AnalysisPercent,
                analysisLengthLimitOverride ?? configuration.AnalysisLengthLimit,
                configuration.PreferredAudioLanguage,
                configuration.PreferAudioStreamWithMostChannels,
                configuration.ChapterAnalyzerIntroductionPattern,
                configuration.FullLengthChapters,
                configuration.EnableSponsorBlockChapterDetection,
                action switch
                {
                    AnalyzerAction.Chapter => false,
                    AnalyzerAction.Chromaprint => true,
                    _ => configuration.PreferChromaprint,
                },
                configuration.AdjustIntroBasedOnChapters,
                configuration.AdjustIntroBasedOnSilence,
                configuration.SnapToKeyframe,
                configuration.EndSnapThreshold,
                configuration.AdjustWindowInward,
                configuration.AdjustWindowOutward,
                configuration.IntroEndOffset,
                configuration.IntroStartOffset,
                configuration.IncludeIntroStartOffsetWhenSnapping,
                configuration.MaximumFingerprintPointDifferences,
                configuration.MaximumTimeSkip,
                configuration.InvertedIndexShift,
                configuration.SilenceDetectionMaximumNoise,
                configuration.SilenceDetectionMinimumDuration);
    }
}
