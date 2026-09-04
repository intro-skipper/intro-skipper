import type { EpisodeItem, ApiResult, SegmentDto } from "../types.ts";
import { getEpisodes } from "../store/jellyfin-client.ts";
import * as api from "../store/api.ts";
import { mapWithConcurrency } from "../utils.ts";

const SEGMENT_FETCH_CONCURRENCY = 6;

export async function getEpisodesWithSegments(
    showId: string,
    seasonId: string,
): Promise<{
    episodes: EpisodeItem[];
    segments: Array<ApiResult<SegmentDto[]> | null>;
    disabledItemIds: string[] | null;
}> {
    const episodes = await getEpisodes(showId, seasonId);

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

// Distinguishes loaded-empty from state-unknown: a failed fetch returns null so
// callers hide the toggles instead of rendering every item as enabled.
export async function getDisabledItemIds(seasonId: string): Promise<string[] | null> {
    const result = await api.getDisabledItems(seasonId);
    return result.ok && result.data ? result.data : null;
}
