// Shared types for plugin configuration, Jellyfin API payloads, and UI wiring.
export interface PluginConfig {
    // Numeric settings persisted in the plugin configuration.
    MaxParallelism: number;
    AnalysisPercent: number;
    SettledSeasonDelayHours: number;
    AnalysisLengthLimit: number;
    MinimumIntroDuration: number;
    MaximumIntroDuration: number;
    MinimumCreditsDuration: number;
    MaximumCreditsDuration: number;
    MaximumMovieCreditsDuration: number;
    MinimumRecapDuration: number;
    MaximumRecapDuration: number;
    MinimumRecapDetectionDuration: number;
    MaximumRecapDetectionDuration: number;
    MinimumPreviewDuration: number;
    MaximumPreviewDuration: number;
    MinimumCommercialDuration: number;
    MaximumCommercialDuration: number;
    ProcessThreads: number;
    IntroEndOffset: number;
    IntroStartOffset: number;
    SkipbuttonHideDelay: number;
    SkipButtonVisibleSeconds: number;
    SilenceDetectionMaximumNoise: number;
    SilenceDetectionMinimumDuration: number;
    BlackFrameMinimumPercentage: number;
    BlackFrameThreshold: number;
    AdjustWindowInward: number;
    AdjustWindowOutward: number;
    EndSnapThreshold: number;

    // String settings persisted in the plugin configuration.
    ProcessPriority: string;
    CacheCompressionLevel: "NoCompression" | "Fastest" | "Optimal" | "SmallestSize";
    ChapterAnalyzerIntroductionPattern: string;
    ChapterAnalyzerEndCreditsPattern: string;
    ChapterAnalyzerPreviewPattern: string;
    ChapterAnalyzerRecapPattern: string;
    ChapterAnalyzerCommercialPattern: string;
    ExcludeSeries: string;
    SeriesExclusions: string[];
    MovieExclusions: string[];
    PathExclusions: string[];

    // Feature toggles persisted in the plugin configuration.
    AutoDetectIntros: boolean;
    ReanalyzeSettledSeasons: boolean;
    AnalyzeSeasonZero: boolean;
    UpdateMediaSegments: boolean;
    UseAlternativeBlackFrameAnalyzer: boolean;
    RefineCreditsBoundary: boolean;
    DetectNonBlackCredits: boolean;
    UseChapterMarkersBlackFrame: boolean;
    FullLengthChapters: boolean;
    EnableSponsorBlockChapterDetection: boolean;
    SkipFirstEpisode: boolean;
    SkipFirstEpisodeAnime: boolean;
    AnimePreviewFromCreditsEnd: boolean;
    ScanIntroduction: boolean;
    ScanCredits: boolean;
    ScanRecap: boolean;
    DetectRecapUsingBlackFrames: boolean;
    ScanPreview: boolean;
    ScanCommercial: boolean;
    EnableMainMenu: boolean;
    PreferChromaprint: boolean;
    ProbeAudioDuration: boolean;
    SnapToKeyframe: boolean;
    AdjustIntroBasedOnSilence: boolean;
    AdjustIntroBasedOnChapters: boolean;
    UseFileTransformationPlugin: boolean;
    AutoSkipIntro: boolean;
    AutoSkipCredits: boolean;

    // Server-managed flag exposed to the dashboard.
    readonly FileTransformationPluginEnabled: boolean;
}

// API responses and timestamp domain models.
export type ApiResult<T> = {
    ok: boolean;
    status: number | null;
    data?: T;
    error?: string;
};

export type AnalyzerActions = {
    Introduction?: string;
    Credits?: string;
    Recap?: string;
    Preview?: string;
    Commercial?: string;
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

// Shared options for generated form controls.
export interface FieldOptions<T> {
    id: string;
    label: string;
    description?: string;
    warning?: string;
    validate?: (value: T) => string | null;
    disabled?: () => boolean;
    visible?: () => boolean;
    onChange?: (value: T) => void;
}

export type CheckboxFieldOptions = FieldOptions<boolean>;

export type NumberFieldOptions = FieldOptions<number> & {
    min?: number;
    max?: number;
    step?: number;
};

export type TextFieldOptions = FieldOptions<string> & {
    placeholder?: string;
};

export type SelectFieldOptions = FieldOptions<string> & {
    options: Array<{ value: string; label: string }>;
};

// Store and routing contracts used across tabs.
export type StoreEvent = "loaded" | "changed" | "saved" | "validation";

export interface Tab {
    id: string;
    label: string;
    render: (container: HTMLElement) => void;
    destroy?: () => void;
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
