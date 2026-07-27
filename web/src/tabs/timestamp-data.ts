import type { ShowItem, SeasonItem, EpisodeItem, ApiResult, TimestampMap } from "../types.ts";
import * as jellyfinClient from "../store/jellyfin-client.ts";
import * as api from "../store/api.ts";
import { mapWithConcurrency } from "../utils.ts";

const TIMESTAMP_FETCH_CONCURRENCY = 6;

export type LibraryItem = { Id: string; Name: string };

export function getLibraries(): Promise<LibraryItem[]> {
    return jellyfinClient.getLibraries();
}

export function getShowsInLibrary(libraryId: string, libraryName: string): Promise<ShowItem[]> {
    return jellyfinClient.getShowsInLibrary(libraryId, libraryName);
}

export function getSeasons(showId: string): Promise<SeasonItem[]> {
    return jellyfinClient.getSeasons(showId);
}

export async function getEpisodesWithTimestamps(
    showId: string,
    seasonId: string,
): Promise<{
    episodes: EpisodeItem[];
    timestamps: Array<ApiResult<TimestampMap> | null>;
    disabledEpisodeIds: string[];
}> {
    const episodes = await jellyfinClient.getEpisodes(showId, seasonId);

    if (episodes.length === 0) {
        return { episodes: [], timestamps: [], disabledEpisodeIds: [] };
    }

    const [timestamps, disabledResult] = await Promise.all([
        mapWithConcurrency(episodes, TIMESTAMP_FETCH_CONCURRENCY, (ep) => api.getEpisodeTimestamps(ep.Id)),
        api.getMediaSegmentExcludedEpisodes(seasonId),
    ]);

    return {
        episodes,
        timestamps,
        disabledEpisodeIds: disabledResult.ok && disabledResult.data ? disabledResult.data : [],
    };
}

export async function getMovieTimestamps(showId: string): Promise<ApiResult<TimestampMap>> {
    return api.getEpisodeTimestamps(showId);
}
