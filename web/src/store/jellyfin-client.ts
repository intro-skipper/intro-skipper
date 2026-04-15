import { Jellyfin } from "@jellyfin/sdk";
import { getItemsApi, getUserViewsApi, getTvShowsApi } from "@jellyfin/sdk/lib/utils/api";
import { BaseItemKind, ItemSortBy } from "@jellyfin/sdk/lib/generated-client/models";
import type { Api } from "@jellyfin/sdk";
import type { LibraryInfo, ShowItem, SeasonItem, EpisodeItem, SupportedCollectionType } from "../types.ts";

let api: Api | null = null;

function getApi(): Api {
  if (api) return api;
  const jellyfin = new Jellyfin({
    clientInfo: { name: "Intro Skipper Config", version: "1.0.0" },
    deviceInfo: { name: "Web Browser", id: "intro-skipper-config" },
  });
  api = jellyfin.createApi(window.ApiClient.serverAddress(), window.ApiClient.accessToken());
  return api;
}

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

export async function getLibraries(): Promise<LibraryInfo[]> {
  const result = await getUserViewsApi(getApi()).getUserViews();
  const items = result.data.Items ?? [];
  return items
    .filter((item) => item.Id && isSupportedCollectionType(item.CollectionType))
    .map((item) => ({
      Id: item.Id!,
      Name: item.Name ?? "Unknown",
      CollectionType: (item.CollectionType ?? null) as SupportedCollectionType,
    }));
}

export async function getShowsInLibrary(
  libraryId: string,
  libraryName: string,
): Promise<ShowItem[]> {
  const result = await getItemsApi(getApi()).getItems({
    parentId: libraryId,
    includeItemTypes: [BaseItemKind.Series, BaseItemKind.Movie],
    sortBy: [ItemSortBy.SortName],
    sortOrder: ["Ascending"],
    recursive: true,
  });
  return (result.data.Items ?? [])
    .filter((item) => item.Id)
    .map((item) => ({
      Id: item.Id!,
      Name: item.Name ?? "Unknown",
      ProductionYear: item.ProductionYear ?? null,
      Type: item.Type === BaseItemKind.Movie ? "Movie" : "Series",
      LibraryId: libraryId,
      LibraryName: libraryName,
    }));
}

export async function getSeasons(seriesId: string): Promise<SeasonItem[]> {
  const result = await getTvShowsApi(getApi()).getSeasons({ seriesId });
  return (result.data.Items ?? [])
    .filter((item) => item.Id)
    .map((item) => ({
      Id: item.Id!,
      Name: item.Name ?? "Unknown",
      IndexNumber: item.IndexNumber ?? null,
    }));
}

export async function getEpisodes(seriesId: string, seasonId: string): Promise<EpisodeItem[]> {
  const result = await getTvShowsApi(getApi()).getEpisodes({
    seriesId,
    seasonId,
    enableImages: true,
  });
  return (result.data.Items ?? [])
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
