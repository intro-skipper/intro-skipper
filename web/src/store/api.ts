import type {
    PluginConfig,
    ApiResult,
    SegmentDto,
    SegmentCreateRequest,
    SegmentUpdateRequest,
    AnalyzerActions,
    ScanStatus,
    PluginInfo,
    LibraryStorage,
    SystemStorageInfo,
    ClearExcludedTimestampsResponse,
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

    if (method === "POST" || method === "PUT") {
        headers["Content-Type"] = "application/json";
    }

    return await fetch(fullUrl, { method, headers, body });
}

export function getJson<T>(url: string): Promise<ApiResult<T>> {
    return request<T>(url, "GET");
}

// Plugin configuration.
export function loadPluginConfig(): Promise<PluginConfig> {
    return window.ApiClient.getPluginConfiguration(PLUGIN_ID);
}

export function savePluginConfig(config: PluginConfig): Promise<unknown> {
    return window.ApiClient.updatePluginConfiguration(PLUGIN_ID, config);
}

// Extracts the most useful error text from an ASP.NET error payload.
async function readErrorMessage(response: Response): Promise<string> {
    try {
        const data: unknown = await response.json();
        if (typeof data === "string" && data.length > 0) {
            return data;
        }
        if (typeof data === "object" && data !== null) {
            for (const key of ["title", "detail", "Message"]) {
                const value = Reflect.get(data, key);
                if (typeof value === "string" && value.length > 0) {
                    return value;
                }
            }
        }
    } catch {
        // Fall through to the generic message.
    }
    return "Server returned " + response.status;
}

// Shared request envelope: JSON body in (when given), ApiResult out. A 204
// response carries no body and maps to null data (the DELETE case).
async function request<T>(
    url: string,
    method: "GET" | "POST" | "PUT" | "DELETE",
    body?: unknown,
): Promise<ApiResult<T>> {
    try {
        const response = await fetchWithAuth(
            url,
            method,
            body === undefined ? null : JSON.stringify(body),
        );
        if (response.ok) {
            return {
                ok: true,
                status: response.status,
                data: (response.status === 204 ? null : await response.json()) as T,
            };
        }
        return {
            ok: false,
            status: response.status,
            error: await readErrorMessage(response),
        };
    } catch (err: unknown) {
        return {
            ok: false,
            status: null,
            error: err instanceof Error ? err.message : "Network error",
        };
    }
}

// Segment browsing and editing (plural segments API). Suppressed (tombstoned)
// segments are included so the editor can offer Restore; display code filters them.
export function getEpisodeSegments(
    itemId: string,
    includeSuppressed = true,
): Promise<ApiResult<SegmentDto[]>> {
    return getJson<SegmentDto[]>(
        `Episode/${encodeURIComponent(itemId)}/Segments?includeSuppressed=${includeSuppressed}`,
    );
}

export function createEpisodeSegment(
    itemId: string,
    body: SegmentCreateRequest,
): Promise<ApiResult<SegmentDto>> {
    return request<SegmentDto>(`Episode/${encodeURIComponent(itemId)}/Segments`, "POST", body);
}

export function updateEpisodeSegment(
    itemId: string,
    segmentId: string,
    body: SegmentUpdateRequest,
): Promise<ApiResult<SegmentDto>> {
    return request<SegmentDto>(
        `Episode/${encodeURIComponent(itemId)}/Segments/${encodeURIComponent(segmentId)}`,
        "PUT",
        body,
    );
}

export function deleteEpisodeSegment(itemId: string, segmentId: string): Promise<ApiResult<null>> {
    return request<null>(
        `Episode/${encodeURIComponent(itemId)}/Segments/${encodeURIComponent(segmentId)}`,
        "DELETE",
    );
}

export function restoreEpisodeSegment(
    itemId: string,
    segmentId: string,
): Promise<ApiResult<SegmentDto>> {
    return request<SegmentDto>(
        `Episode/${encodeURIComponent(itemId)}/Segments/${encodeURIComponent(segmentId)}/Restore`,
        "POST",
    );
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

// Per-item media-segment disable: a disabled item's automatic segments are
// withheld from Jellyfin while user segments keep syncing. The listing is keyed
// by the season-state key (a movie's own ID for movies); mutations name only
// the item and the server resolves the owning key itself.
export function getDisabledItems(seasonId: string): Promise<ApiResult<string[]>> {
    return getJson<string[]>(`Intros/DisabledItems/${encodeURIComponent(seasonId)}`);
}

export function setItemDisabled(itemId: string, disabled: boolean): Promise<ApiResult<null>> {
    return request<null>(
        `Intros/DisabledItems/${encodeURIComponent(itemId)}`,
        disabled ? "PUT" : "DELETE",
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

function hasNumberProperty(target: object, property: keyof ClearExcludedTimestampsResponse): boolean {
    return typeof Reflect.get(target, property) === "number";
}

function isClearExcludedTimestampsResponse(
    value: unknown,
): value is ClearExcludedTimestampsResponse {
    return (
        typeof value === "object" &&
        value !== null &&
        hasNumberProperty(value, "AffectedItems") &&
        hasNumberProperty(value, "RemovedSegments") &&
        hasNumberProperty(value, "RemovedCacheEntries")
    );
}

export async function clearExcludedTimestamps(): Promise<
    ApiResult<ClearExcludedTimestampsResponse>
> {
    const result = await request<unknown>("Intros/ExcludedTimestamps/Clear", "POST");
    if (!result.ok) {
        return { ok: false, status: result.status, error: result.error };
    }

    if (!isClearExcludedTimestampsResponse(result.data)) {
        return {
            ok: false,
            status: result.status,
            error: "Unexpected clear response shape",
        };
    }

    return {
        ok: true,
        status: result.status,
        data: result.data,
    };
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
