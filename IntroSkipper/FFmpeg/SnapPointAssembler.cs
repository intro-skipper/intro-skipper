// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.Db;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Assembles boundary-snapping data from the detection cache. Owns the cache-layer
/// knowledge the payloads encode: silence and keyframe payloads are cached in absolute
/// seconds, while black-interval payloads are relative to their analysis run's credits
/// scan start, which is not stored with the row and must be recovered per row from a
/// sibling anchor row or the live analysis queue — or the intervals are omitted rather
/// than served wrong.
/// </summary>
internal static class SnapPointAssembler
{
    // Payload times of a black-interval row must fall inside the row's scanned window;
    // the tolerance absorbs blackdetect's boundary rounding at the window edges.
    private const double AnchorToleranceSeconds = 0.5;

    // Keyframes from overlapping scan ranges repeat; positions closer than this are one keyframe.
    private const double KeyframeDedupeToleranceSeconds = 0.001;

    /// <summary>
    /// Builds the snapping data for an item from the detection cache. Best-effort: arrays
    /// are empty when no cached detection data exists.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cacheDatabase">Detection cache database facade.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The snapping data.</returns>
    internal static async Task<SnapPointsResponse> BuildAsync(Guid itemId, IDetectionCacheDatabase cacheDatabase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cacheDatabase);

        var entries = await cacheDatabase
            .GetEntriesForItemAsync(
                itemId,
                [CacheEntryType.Keyframe, CacheEntryType.Silence, CacheEntryType.BlackInterval],
                cancellationToken)
            .ConfigureAwait(false);

        var fromCache = entries.Count > 0;
        var keyframes = new List<double>();
        var silence = new List<SnapRange>();
        var blackIntervals = new List<SnapRange>();

        foreach (var entry in entries.Where(e => e.Type == CacheEntryType.Keyframe))
        {
            var payload = DetectionCacheService.DecompressBrotli<double[]>(entry.Data);
            if (payload is not null)
            {
                keyframes.AddRange(payload);
            }
        }

        foreach (var entry in entries.Where(e => e.Type == CacheEntryType.Silence))
        {
            var payload = DetectionCacheService.DecompressBrotli<TimeRange[]>(entry.Data);
            if (payload is not null)
            {
                silence.AddRange(payload.Select(range => new SnapRange(range.Start, range.End)));
            }
        }

        var intervalEntries = entries.Where(e => e.Type == CacheEntryType.BlackInterval).ToList();
        if (intervalEntries.Count > 0)
        {
            // Anchor recovery is deferred until interval rows exist: it costs a second
            // cache query and, as a last resort, a scan of the analysis queue.
            var anchorCandidates = await CollectAnchorCandidatesAsync(itemId, cacheDatabase, cancellationToken).ConfigureAwait(false);
            foreach (var entry in intervalEntries)
            {
                var payload = DetectionCacheService.DecompressBrotli<BlackInterval[]>(entry.Data);
                if (payload is null || payload.Length == 0)
                {
                    continue;
                }

                var anchor = ResolveAnchorForRow(entry, payload, anchorCandidates);
                if (anchor is null)
                {
                    // Unrecoverable or ambiguous anchor — omit rather than serve wrong data.
                    continue;
                }

                foreach (var interval in payload)
                {
                    blackIntervals.Add(new SnapRange(interval.Start + anchor.Value, interval.End + anchor.Value));
                }
            }
        }

        keyframes.Sort();
        var dedupedKeyframes = new List<double>(keyframes.Count);
        foreach (var keyframe in keyframes)
        {
            if (dedupedKeyframes.Count == 0 || keyframe - dedupedKeyframes[^1] > KeyframeDedupeToleranceSeconds)
            {
                dedupedKeyframes.Add(keyframe);
            }
        }

        return new SnapPointsResponse(
            dedupedKeyframes,
            blackIntervals.OrderBy(range => range.Start).ToList(),
            silence.Distinct().OrderBy(range => range.Start).ToList(),
            fromCache);
    }

    /// <summary>
    /// Collects every credits-scan-start candidate for the item: the whole-scan
    /// black-frame anchor rows (keyed <c>(Credits, BlackFrame, Start = scan start, End = 0)</c>),
    /// the keyframe-visual rows (whose range starts at the scan start), and the live
    /// queue's value. Multiple candidates can exist when analysis ran under different
    /// configurations; only the key columns are needed, so the payload BLOBs are skipped.
    /// </summary>
    private static async Task<List<AnchorCandidate>> CollectAnchorCandidatesAsync(Guid itemId, IDetectionCacheDatabase cacheDatabase, CancellationToken cancellationToken)
    {
        var anchorRanges = await cacheDatabase
            .GetEntryRangesForItemAsync(itemId, [CacheEntryType.BlackFrame, CacheEntryType.KeyframeVisual], cancellationToken)
            .ConfigureAwait(false);

        var candidates = new List<AnchorCandidate>();
        foreach (var range in anchorRanges)
        {
            if (range.Mode != AnalysisMode.Credits)
            {
                continue;
            }

            if (range.Type == CacheEntryType.BlackFrame && range.End > 0)
            {
                continue;
            }

            candidates.Add(new AnchorCandidate(range.Start, range.ConfigHash));
        }

        if (Plugin.Instance is { } plugin)
        {
            foreach (var episodes in plugin.QueuedMediaItems.Values)
            {
                var queued = episodes.FirstOrDefault(episode => episode.EpisodeId == itemId);
                if (queued is not null)
                {
                    candidates.Add(new AnchorCandidate(queued.CreditsFingerprintStart, null));
                    break;
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// Picks the anchor for one black-interval row. A candidate fits when every payload
    /// interval it produces stays inside the row's scanned window; when several distinct
    /// anchors fit (wide windows make cross-era offsets geometrically plausible), only a
    /// unique same-config-hash candidate is trusted — otherwise the row is omitted.
    /// </summary>
    private static double? ResolveAnchorForRow(DbDetectionCache row, BlackInterval[] payload, List<AnchorCandidate> candidates)
    {
        var fitting = candidates
            .Where(candidate => payload.All(interval =>
                interval.Start + candidate.Value >= row.Start - AnchorToleranceSeconds
                && interval.End + candidate.Value <= row.End + AnchorToleranceSeconds))
            .ToList();

        var distinctValues = fitting.Select(candidate => candidate.Value).Distinct().ToList();
        if (distinctValues.Count == 1)
        {
            return distinctValues[0];
        }

        if (distinctValues.Count == 0)
        {
            return null;
        }

        var sameEraValues = fitting
            .Where(candidate => string.Equals(candidate.ConfigHash, row.ConfigHash, StringComparison.Ordinal))
            .Select(candidate => candidate.Value)
            .Distinct()
            .ToList();

        return sameEraValues.Count == 1 ? sameEraValues[0] : null;
    }

    private sealed record AnchorCandidate(double Value, string? ConfigHash);
}
