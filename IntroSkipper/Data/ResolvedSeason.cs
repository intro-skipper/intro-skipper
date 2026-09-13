// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// One season as the analyzers see it. A movie is a season of one whose key and series
/// id are both the movie's id. Episodes carry per-run analysis state and are not shared
/// between resolutions.
/// </summary>
/// <param name="Key">The season key the season's analysis state is stored under.</param>
/// <param name="SeriesId">The id of the series the season belongs to.</param>
/// <param name="Episodes">The season's episodes in analysis order; empty for a known season with nothing to analyze.</param>
internal sealed record ResolvedSeason(Guid Key, Guid SeriesId, IReadOnlyList<QueuedEpisode> Episodes);

/// <summary>
/// Every season of every enabled library, with the number of libraries and series that
/// could not be enumerated or resolved. A non-zero count means the seasons are incomplete,
/// so cleanup must not treat rows absent from them as stale.
/// </summary>
/// <param name="Seasons">The resolved seasons.</param>
/// <param name="Failures">The number of libraries and series that failed.</param>
internal sealed record LibraryResolution(IReadOnlyList<ResolvedSeason> Seasons, int Failures);

/// <summary>
/// One season as the dashboard shows it: a movie, or the episodes Jellyfin shows under a
/// season, which with specials shown within seasons lists an in-season special under its
/// host season as well as under Season 0. The shown items include the ones analysis skips
/// (excluded, without a path), so a flag on any of them can be listed. The episodes carry
/// the key of the season each is analyzed in, so an erase acts on what the dashboard
/// shows and a scan runs the seasons those episodes are analyzed in.
/// </summary>
/// <param name="SeriesId">The id of the series the season belongs to; a movie's own id for a movie.</param>
/// <param name="ItemIds">Every item shown, eligible for analysis or not.</param>
/// <param name="Episodes">The shown episodes eligible for analysis; empty for a known season with nothing to analyze.</param>
internal sealed record DisplayedSeason(Guid SeriesId, IReadOnlyList<Guid> ItemIds, IReadOnlyList<QueuedEpisode> Episodes);
