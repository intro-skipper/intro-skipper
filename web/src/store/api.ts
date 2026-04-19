import type {
    PluginConfig,
    ApiResult,
    TimestampMap,
    AnalyzerActions,
    ScanStatus,
    PluginInfo,
    LibraryStorage,
    SystemStorageInfo,
} from "../types.ts";

const PLUGIN_ID = "c83d86bb-a1e0-4c35-a113-e2101cf4ee6b";

// Shared API helpers for the Intro Skipper dashboard.
export async function fetchWithAuth(
    url: string,
    method: string,
    body?: string | null,
): Promise<Response> {
    const address = window.ApiClient.serverAddress().replace(/\/+$/, "");
    const fullUrl = address + "/" + url;

    const headers: Record<string, string> = {
        Authorization: "MediaBrowser Token=" + window.ApiClient.accessToken(),
    };

    if (method === "POST") {
        headers["Content-Type"] = "application/json";
    }

    return await fetch(fullUrl, { method, headers, body });
}

export async function getJson<T>(url: string): Promise<ApiResult<T>> {
    try {
        const response = await fetchWithAuth(url, "GET");
        if (response.ok) {
            return {
                ok: true,
                status: response.status,
                data: (await response.json()) as T,
            };
        }
        return {
            ok: false,
            status: response.status,
            error: "Server returned " + response.status,
        };
    } catch (err: unknown) {
        return {
            ok: false,
            status: null,
            error: err instanceof Error ? err.message : "Network error",
        };
    }
}

// Plugin configuration.
export function loadPluginConfig(): Promise<PluginConfig> {
    return window.ApiClient.getPluginConfiguration(PLUGIN_ID);
}

export function savePluginConfig(config: PluginConfig): Promise<unknown> {
    return window.ApiClient.updatePluginConfiguration(PLUGIN_ID, config);
}

// Timestamp browsing.
export function getEpisodeTimestamps(episodeId: string): Promise<ApiResult<TimestampMap>> {
    return getJson<TimestampMap>(`Episode/${encodeURIComponent(episodeId)}/Timestamps`);
}

// Per-season analyzer actions.
export function getAnalyzerActions(seasonId: string): Promise<ApiResult<AnalyzerActions>> {
    return getJson<AnalyzerActions>(`Intros/AnalyzerActions/${encodeURIComponent(seasonId)}`);
}

export function updateAnalyzerActions(id: string, actions: AnalyzerActions): Promise<Response> {
    return fetchWithAuth(
        "Intros/AnalyzerActions/UpdateSeason",
        "POST",
        JSON.stringify({ id, analyzerActions: actions }),
    );
}

// Scan controls.
export function scanSeason(showId: string, seasonId: string): Promise<Response> {
    return fetchWithAuth(
        `Intros/ScanSeason/${encodeURIComponent(showId)}/${encodeURIComponent(seasonId)}`,
        "POST",
    );
}

export function getScanStatus(): Promise<ApiResult<ScanStatus>> {
    return getJson<ScanStatus>("Intros/ScanStatus");
}

// Timestamp deletion.
export function eraseTimestamps(mode: string, eraseCache: boolean): Promise<Response> {
    return fetchWithAuth(
        `Intros/EraseTimestamps?mode=${encodeURIComponent(mode)}&eraseCache=${eraseCache}`,
        "POST",
    );
}

export function eraseItemTimestamps(urlPath: string, eraseCache: boolean): Promise<Response> {
    return fetchWithAuth(`${urlPath}?eraseCache=${eraseCache}`, "DELETE");
}

// Support and storage tools.
export async function getSupportBundle(): Promise<string> {
    const response = await fetchWithAuth("IntroSkipper/SupportBundle", "GET");
    if (!response.ok) {
        throw new Error("Failed to fetch support bundle (HTTP " + response.status + ")");
    }
    return response.text();
}

export async function getStorageUsage(): Promise<LibraryStorage[]> {
    const response = await fetchWithAuth("System/Info/Storage", "GET");
    if (!response.ok) {
        throw new Error("Failed to fetch storage usage (HTTP " + response.status + ")");
    }
    const data = (await response.json()) as SystemStorageInfo;
    if (!Array.isArray(data?.Libraries)) {
        throw new Error("Unexpected storage response shape");
    }
    return data.Libraries;
}

// Database maintenance.
export function rebuildDatabase(): Promise<Response> {
    return fetchWithAuth("Intros/RebuildDatabase", "POST");
}

// Skip button web patch helpers.
export function injectSkipButtonCss(): Promise<Response> {
    return fetchWithAuth("SkipButtonCss/InjectCss", "POST");
}

export function updateSkipDuration(): Promise<Response> {
    return fetchWithAuth("SkipButtonCss/UpdateSkipDuration", "POST");
}

// Plugin discovery.
export async function checkPlugins(): Promise<PluginInfo[]> {
    const response = await fetchWithAuth("Plugins", "GET");
    if (!response.ok) {
        throw new Error("Failed to fetch plugins (HTTP " + response.status + ")");
    }
    return (await response.json()) as PluginInfo[];
}
