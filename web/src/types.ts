// Shared types for Jellyfin API payloads and UI wiring. The plugin configuration
// itself is declared in config/schema.ts.
import type { PluginConfig } from "./config/schema.ts";

// API responses and timestamp domain models.

// The answer to one request. `ok` narrows: a success carries the parsed body,
// a failure carries the error text and the HTTP status, or null when the
// request never reached the server.
export type ApiResult<T> =
    | { ok: true; status: number; data: T }
    | { ok: false; status: number | null; error: string };

export type AnalyzerActions = {
    Introduction?: string;
    Credits?: string;
    Recap?: string;
    Preview?: string;
    Commercial?: string;
};

export type AnalysisOverrides = {
    AnalysisPercent: number | null;
    AnalysisLengthLimit: number | null;
    PreviewFromCreditsEnd: boolean | null;
};

// One stored segment as returned by the plural segments API. The Id is shared with
// the Jellyfin media segment row; boundaries are seconds.
export type SegmentType = "Introduction" | "Credits" | "Recap" | "Preview" | "Commercial";

export type SegmentDto = {
    Id: string;
    Type: SegmentType;
    Start: number;
    End: number;
    Source: string;
    Suppressed: boolean;
};

// Wire body of a 202 Accepted mutation response: the change committed durably but
// its Jellyfin projection is pending (or skipped while mirroring is disabled) and
// converges from the server-side journal. Segments carries the committed values.
export type SegmentChangeAcceptedResponse = {
    ChangeStatus: string;
    Projection: "Pending" | "Skipped";
    Segments: SegmentDto[];
};

export type SegmentCreateRequest = {
    Type: SegmentType;
    Start: number;
    End: number;
};

export type SegmentUpdateRequest = {
    Start: number;
    End: number;
};

export type ScanStatus = {
    isRunning: boolean;
    isQueued: boolean;
    failed: boolean;
};

export type PluginInfo = {
    Id: string;
    Status: string;
};

type StorageFolder = {
    Path: string;
    FreeSpace: number;
    UsedSpace: number;
    StorageType: string;
    DeviceId: string;
};

export type LibraryStorage = {
    Id: string;
    Name: string;
    Folders: StorageFolder[];
};

export type SystemStorageInfo = {
    Libraries: LibraryStorage[];
};

export type ClearExcludedTimestampsResponse = {
    AffectedItems: number;
    RemovedSegments: number;
    RemovedCacheEntries: number;
};

// Support bundle returned by IntroSkipper/SupportBundle/Json. A section holds
// either Entries (facts) or Text (a preformatted block); collapsed sections are
// noise that stays folded until expanded.
export type SupportBundleEntry = {
    Label: string;
    Value: string;
};

export type SupportBundleSection = {
    Title: string;
    Collapsed: boolean;
    Entries?: SupportBundleEntry[] | null;
    Text?: string | null;
};

export type SupportBundle = {
    Markdown: string;
    Sections: SupportBundleSection[];
};

// Raw Jellyfin API response shapes (only the fields we actually read).
export type JellyfinItemsResponse<T> = {
    Items?: T[];
};

export type JellyfinLibraryItem = {
    Id?: string;
    Name?: string;
    CollectionType?: string;
};

export type JellyfinMediaItem = {
    Id?: string;
    Name?: string;
    ProductionYear?: number;
    Type?: string;
};

export type JellyfinSeasonItem = {
    Id?: string;
    Name?: string;
    IndexNumber?: number;
};

export type JellyfinEpisodeItem = {
    Id?: string;
    Name?: string;
    IndexNumber?: number;
    RunTimeTicks?: number;
    SeriesName?: string;
};

// Simplified Jellyfin SDK shapes used by the timestamps UI.
export type SupportedCollectionType = "movies" | "tvshows" | "folders" | null;

export type LibraryInfo = {
    Id: string;
    Name: string;
    CollectionType: SupportedCollectionType;
};

export type ShowItem = {
    Id: string;
    Name: string;
    ProductionYear: number | null;
    Type: string; // Jellyfin returns "Series" for shows and "Movie" for films.
    LibraryId: string;
    LibraryName: string;
};

export type SeasonItem = {
    Id: string;
    Name: string;
    IndexNumber: number | null;
};

export type EpisodeItem = {
    Id: string;
    Name: string;
    IndexNumber: number | null;
    RunTimeTicks: number | null;
    SeriesName: string | null;
};

// Routing contract used across tabs. `signal` aborts when the tab is left, so
// everything the render starts (fetches, listeners, store subscriptions) ends there.
export interface Tab {
    id: string;
    label: string;
    render: (container: HTMLElement, signal: AbortSignal) => void;
}

// Jellyfin injects these globals into the dashboard page.
declare global {
    interface Window {
        ApiClient: {
            serverAddress(): string;
            accessToken(): string;
            getPluginConfiguration(id: string): Promise<PluginConfig>;
            updatePluginConfiguration(id: string, config: PluginConfig): Promise<unknown>;
        };
        Dashboard: {
            showLoadingMsg(): void;
            hideLoadingMsg(): void;
            alert(msg: string): void;
            confirm(body: string, title: string, callback: (result: boolean) => void): void;
            processPluginConfigurationUpdateResult(result: unknown): void;
        };
    }
}
