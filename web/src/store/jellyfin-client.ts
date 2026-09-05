import { getJson } from "./api.ts";
import type {
    JellyfinItemsResponse,
    JellyfinLibraryItem,
    JellyfinMediaItem,
    JellyfinSeasonItem,
    JellyfinEpisodeItem,
    LibraryInfo,
    ShowItem,
    SeasonItem,
    EpisodeItem,
    SupportedCollectionType,
} from "../types.ts";

// Libraries whose collection type explicitly targets movies or TV shows, plus
// "folders" and untyped (null/undefined) views which cover VFS-backed libraries
// that Jellyfin exposes without a specific CollectionType but may still contain
// shows and movies analysed by the plugin.
const SUPPORTED_COLLECTION_TYPES = new Set<string>(["movies", "tvshows", "folders"]);

function isSupportedCollectionType(
    collectionType: string | null | undefined,
): collectionType is SupportedCollectionType {
    return collectionType == null || SUPPORTED_COLLECTION_TYPES.has(collectionType);
}

// Library and show listings are shared by the exclusion suggestions and the
// timestamps browser. Entries older than LISTING_TTL_MS are refetched on the next
// read, so new media shows up without a page reload while switching tabs stays
// cheap (the timestamps tab lists every library's shows on open). Only successful
// responses are kept, so a failed request is retried on the next call.
const LISTING_TTL_MS = 60_000;

type Listing<T> = { loadedAt: number; value: Promise<T> };

function fresh<T>(listing: Listing<T> | undefined): Promise<T> | null {
    return listing && Date.now() - listing.loadedAt < LISTING_TTL_MS ? listing.value : null;
}

let librariesCache: Listing<LibraryInfo[]> | undefined;
const showsByLibrary = new Map<string, Listing<ShowItem[]>>();

export function getLibraries(): Promise<LibraryInfo[]> {
    const cached = fresh(librariesCache);
    if (cached) return cached;

    const loading = getJson<JellyfinItemsResponse<JellyfinLibraryItem>>("UserViews").then(
        (result) => {
            if (!result.ok) {
                librariesCache = undefined;
                console.error("Failed to load libraries", result.error);
                return [];
            }
            return (result.data?.Items ?? [])
                .filter((item) => item.Id && isSupportedCollectionType(item.CollectionType))
                .map((item) => ({
                    Id: item.Id!,
                    Name: item.Name ?? "Unknown",
                    CollectionType: (item.CollectionType ?? null) as SupportedCollectionType,
                }));
        },
    );
    librariesCache = { loadedAt: Date.now(), value: loading };
    return loading;
}

export function getShowsInLibrary(libraryId: string, libraryName: string): Promise<ShowItem[]> {
    const cached = fresh(showsByLibrary.get(libraryId));
    if (cached) return cached;

    const params = new URLSearchParams({
        parentId: libraryId,
        includeItemTypes: "Series,Movie",
        sortBy: "SortName",
        sortOrder: "Ascending",
        recursive: "true",
        // The listing needs names and years only; thumbnails are built by URL.
        enableImages: "false",
    });
    const loading = getJson<JellyfinItemsResponse<JellyfinMediaItem>>(
        `Items?${params.toString()}`,
    ).then((result) => {
        if (!result.ok) {
            showsByLibrary.delete(libraryId);
            console.error("Failed to load shows for library", libraryId, result.error);
            return [];
        }
        return (result.data?.Items ?? [])
            .filter((item) => item.Id)
            .map((item) => ({
                Id: item.Id!,
                Name: item.Name ?? "Unknown",
                ProductionYear: item.ProductionYear ?? null,
                Type: item.Type === "Movie" ? "Movie" : "Series",
                LibraryId: libraryId,
                LibraryName: libraryName,
            }));
    });
    showsByLibrary.set(libraryId, { loadedAt: Date.now(), value: loading });
    return loading;
}

// Every show and movie across the supported libraries.
export async function getAllShows(): Promise<ShowItem[]> {
    const libraries = await getLibraries();
    const groups = await Promise.all(
        libraries.map((library) => getShowsInLibrary(library.Id, library.Name)),
    );
    return groups.flat();
}

export async function getSeasons(seriesId: string): Promise<SeasonItem[]> {
    const result = await getJson<JellyfinItemsResponse<JellyfinSeasonItem>>(
        `Shows/${encodeURIComponent(seriesId)}/Seasons`,
    );
    if (!result.ok) {
        console.error("Failed to load seasons for series", seriesId, result.error);
        return [];
    }
    return (result.data?.Items ?? [])
        .filter((item) => item.Id)
        .map((item) => ({
            Id: item.Id!,
            Name: item.Name ?? "Unknown",
            IndexNumber: item.IndexNumber ?? null,
        }));
}

export async function getEpisodes(seriesId: string, seasonId: string): Promise<EpisodeItem[]> {
    const params = new URLSearchParams({
        seasonId,
        enableImages: "true",
    });
    const result = await getJson<JellyfinItemsResponse<JellyfinEpisodeItem>>(
        `Shows/${encodeURIComponent(seriesId)}/Episodes?${params.toString()}`,
    );
    if (!result.ok) {
        console.error("Failed to load episodes for series", seriesId, result.error);
        return [];
    }
    return (result.data?.Items ?? [])
        .filter((item) => item.Id)
        .map((item) => ({
            Id: item.Id!,
            Name: item.Name ?? "Unknown",
            IndexNumber: item.IndexNumber ?? null,
            RunTimeTicks: item.RunTimeTicks ?? null,
            SeriesName: item.SeriesName ?? null,
        }));
}

export function getImageUrl(itemId: string, height = 60): string {
    return (
        window.ApiClient.serverAddress() +
        "/Items/" +
        itemId +
        "/Images/Primary?fillHeight=" +
        height +
        "&quality=90"
    );
}
