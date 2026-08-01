import type { ShowItem, SeasonItem, EpisodeItem, ApiResult, SegmentDto } from "../types.ts";
import * as jellyfinClient from "../store/jellyfin-client.ts";
import * as api from "../store/api.ts";
import { mapWithConcurrency } from "../utils.ts";

const SEGMENT_FETCH_CONCURRENCY = 6;

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

export async function getEpisodesWithSegments(
    showId: string,
    seasonId: string,
): Promise<{
    episodes: EpisodeItem[];
    segments: Array<ApiResult<SegmentDto[]> | null>;
    disabledItemIds: string[] | null;
}> {
    const episodes = await jellyfinClient.getEpisodes(showId, seasonId);

    if (episodes.length === 0) {
        return { episodes: [], segments: [], disabledItemIds: [] };
    }

    const [segments, disabledItemIds] = await Promise.all([
        mapWithConcurrency(episodes, SEGMENT_FETCH_CONCURRENCY, (ep) =>
            api.getEpisodeSegments(ep.Id),
        ),
        getDisabledItemIds(seasonId),
    ]);

    return { episodes, segments, disabledItemIds };
}

export function getMovieSegments(showId: string): Promise<ApiResult<SegmentDto[]>> {
    return api.getEpisodeSegments(showId);
}

// Distinguishes loaded-empty from state-unknown: a failed fetch returns null so
// callers hide the toggles instead of rendering every item as enabled.
export async function getDisabledItemIds(seasonId: string): Promise<string[] | null> {
    const result = await api.getDisabledItems(seasonId);
    return result.ok && result.data ? result.data : null;
}

export function setItemDisabled(itemId: string, disabled: boolean): Promise<ApiResult<null>> {
    return api.setItemDisabled(itemId, disabled);
}
