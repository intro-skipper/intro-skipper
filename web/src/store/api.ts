import type { PluginConfig } from "../config/schema.ts";
import type {
    ApiResult,
    SegmentDto,
    SegmentChangeAcceptedResponse,
    SegmentCreateRequest,
    SegmentUpdateRequest,
    AnalyzerActions,
    AnalysisOverrides,
    ScanStatus,
    PluginInfo,
    LibraryStorage,
    SystemStorageInfo,
    ClearExcludedTimestampsResponse,
    SupportBundle,
} from "../types.ts";

const PLUGIN_ID = "c83d86bb-a1e0-4c35-a113-e2101cf4ee6b";

// Plugin configuration goes through Jellyfin's own client, which handles auth.
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

// Several endpoints answer 200, 202 or 204 with no body at all; those map to
// null data. Anything else must be JSON.
async function readBody(response: Response): Promise<unknown> {
    const text = await response.text();
    return text.length === 0 ? null : JSON.parse(text);
}

type RequestOptions = {
    method?: "GET" | "POST" | "PUT" | "DELETE";
    body?: unknown;
    signal?: AbortSignal;
};

/**
 * The one fetch in the dashboard. Adds the Jellyfin token, sends `body` as
 * JSON, and answers with an ApiResult: `ok: true` with the parsed body (null
 * when the response is empty), or `ok: false` with the server's error text or
 * the network error message. It never throws for a failed request. The one
 * exception is `signal`: when it aborts, the returned promise rejects with the
 * fetch AbortError so the caller's continuation stops.
 */
async function request<T>(url: string, options: RequestOptions = {}): Promise<ApiResult<T>> {
    const method = options.method ?? "GET";
    const address = window.ApiClient.serverAddress().replace(/\/+$/, "");
    const headers: Record<string, string> = {
        Authorization: "MediaBrowser Token=" + window.ApiClient.accessToken(),
    };
    if (method === "POST" || method === "PUT") {
        headers["Content-Type"] = "application/json";
    }

    try {
        const response = await fetch(address + "/" + url, {
            method,
            headers,
            body: options.body === undefined ? null : JSON.stringify(options.body),
            signal: options.signal,
        });
        if (response.ok) {
            return { ok: true, status: response.status, data: (await readBody(response)) as T };
        }
        return { ok: false, status: response.status, error: await readErrorMessage(response) };
    } catch (err: unknown) {
        if (err instanceof DOMException && err.name === "AbortError") throw err;
        return {
            ok: false,
            status: null,
            error: err instanceof Error ? err.message : "Network error",
        };
    }
}

export function getJson<T>(url: string, signal?: AbortSignal): Promise<ApiResult<T>> {
    return request<T>(url, { signal });
}

// Segment browsing and editing (plural segments API). Suppressed (tombstoned)
// segments are included so the editor can offer Restore; display code filters them.
export function getEpisodeSegments(
    itemId: string,
    signal?: AbortSignal,
): Promise<ApiResult<SegmentDto[]>> {
    return getJson<SegmentDto[]>(
        `Episode/${encodeURIComponent(itemId)}/Segments?includeSuppressed=true`,
        signal,
    );
}

// A mutation whose Jellyfin projection did not apply synchronously answers 202 with
// a SegmentChangeAcceptedResponse instead of the segment DTO; the change itself is
// committed and the server converges Jellyfin from its journal. Unwraps that body
// back to the endpoint's single-DTO shape so callers keep one contract.
async function requestSegmentMutation(
    url: string,
    method: "POST" | "PUT",
    body?: unknown,
    signal?: AbortSignal,
): Promise<ApiResult<SegmentDto | undefined>> {
    const result = await request<SegmentDto | SegmentChangeAcceptedResponse>(url, {
        method,
        body,
        signal,
    });
    if (!result.ok) return result;
    const data = "Segments" in result.data ? result.data.Segments[0] : result.data;
    return { ok: true, status: result.status, data };
}

export function createEpisodeSegment(
    itemId: string,
    body: SegmentCreateRequest,
    signal?: AbortSignal,
): Promise<ApiResult<SegmentDto | undefined>> {
    return requestSegmentMutation(
        `Episode/${encodeURIComponent(itemId)}/Segments`,
        "POST",
        body,
        signal,
    );
}

export function updateEpisodeSegment(
    itemId: string,
    segmentId: string,
    body: SegmentUpdateRequest,
    signal?: AbortSignal,
): Promise<ApiResult<SegmentDto | undefined>> {
    return requestSegmentMutation(
        `Episode/${encodeURIComponent(itemId)}/Segments/${encodeURIComponent(segmentId)}`,
        "PUT",
        body,
        signal,
    );
}

export function deleteEpisodeSegment(
    itemId: string,
    segmentId: string,
    signal?: AbortSignal,
): Promise<ApiResult<null>> {
    return request<null>(
        `Episode/${encodeURIComponent(itemId)}/Segments/${encodeURIComponent(segmentId)}`,
        { method: "DELETE", signal },
    );
}

export function restoreEpisodeSegment(
    itemId: string,
    segmentId: string,
    signal?: AbortSignal,
): Promise<ApiResult<SegmentDto | undefined>> {
    return requestSegmentMutation(
        `Episode/${encodeURIComponent(itemId)}/Segments/${encodeURIComponent(segmentId)}/Restore`,
        "POST",
        undefined,
        signal,
    );
}

// Per-season analyzer actions.
export function getAnalyzerActions(
    seasonId: string,
    signal?: AbortSignal,
): Promise<ApiResult<AnalyzerActions>> {
    return getJson<AnalyzerActions>(`Intros/AnalyzerActions/${encodeURIComponent(seasonId)}`, signal);
}

export function updateAnalyzerActions(
    id: string,
    actions: AnalyzerActions,
    signal?: AbortSignal,
): Promise<ApiResult<null>> {
    return request<null>("Intros/AnalyzerActions/UpdateSeason", {
        method: "POST",
        body: { id, analyzerActions: actions },
        signal,
    });
}

export function getAnalysisOverrides(
    seasonId: string,
    signal?: AbortSignal,
): Promise<ApiResult<AnalysisOverrides>> {
    return getJson<AnalysisOverrides>(
        `Intros/AnalysisOverrides/${encodeURIComponent(seasonId)}`,
        signal,
    );
}

export function updateAnalysisOverrides(
    id: string,
    overrides: AnalysisOverrides,
    signal?: AbortSignal,
): Promise<ApiResult<null>> {
    return request<null>("Intros/AnalysisOverrides/UpdateSeason", {
        method: "POST",
        body: { id, ...overrides },
        signal,
    });
}

// Per-item media-segment disable: a disabled item's automatic segments are
// withheld from Jellyfin while user segments keep syncing. The listing is keyed
// by the season-state key (a movie's own ID for movies); mutations name only
// the item and the server resolves the owning key itself.
export function getDisabledItems(
    seasonId: string,
    signal?: AbortSignal,
): Promise<ApiResult<string[]>> {
    return getJson<string[]>(`Intros/DisabledItems/${encodeURIComponent(seasonId)}`, signal);
}

export function setItemDisabled(
    itemId: string,
    disabled: boolean,
    signal?: AbortSignal,
): Promise<ApiResult<null>> {
    return request<null>(`Intros/DisabledItems/${encodeURIComponent(itemId)}`, {
        method: disabled ? "PUT" : "DELETE",
        signal,
    });
}

// Scan controls.
export function scanSeason(
    showId: string,
    seasonId: string,
    signal?: AbortSignal,
): Promise<ApiResult<null>> {
    return request<null>(
        `Intros/ScanSeason/${encodeURIComponent(showId)}/${encodeURIComponent(seasonId)}`,
        { method: "POST", signal },
    );
}

export function getScanStatus(seasonId: string, signal?: AbortSignal): Promise<ApiResult<ScanStatus>> {
    return getJson<ScanStatus>(`Intros/ScanStatus/${encodeURIComponent(seasonId)}`, signal);
}

// Timestamp deletion.
export function eraseTimestamps(
    mode: string,
    eraseCache: boolean,
    signal?: AbortSignal,
): Promise<ApiResult<null>> {
    return request<null>(
        `Intros/EraseTimestamps?mode=${encodeURIComponent(mode)}&eraseCache=${eraseCache}`,
        { method: "POST", signal },
    );
}

export function eraseItemTimestamps(
    urlPath: string,
    eraseCache: boolean,
    signal?: AbortSignal,
): Promise<ApiResult<null>> {
    return request<null>(`${urlPath}?eraseCache=${eraseCache}`, { method: "DELETE", signal });
}

export function clearExcludedTimestamps(
    signal?: AbortSignal,
): Promise<ApiResult<ClearExcludedTimestampsResponse>> {
    return request<ClearExcludedTimestampsResponse>("Intros/ExcludedTimestamps/Clear", {
        method: "POST",
        signal,
    });
}

// Support and storage tools.
export async function getSupportBundle(signal?: AbortSignal): Promise<ApiResult<SupportBundle>> {
    const result = await request<SupportBundle>("IntroSkipper/SupportBundle/Json", { signal });
    if (
        result.ok &&
        (typeof result.data?.Markdown !== "string" || !Array.isArray(result.data.Sections))
    ) {
        return { ok: false, status: result.status, error: "Unexpected support bundle response shape" };
    }
    return result;
}

export async function getStorageUsage(signal?: AbortSignal): Promise<ApiResult<LibraryStorage[]>> {
    const result = await request<SystemStorageInfo>("System/Info/Storage", { signal });
    if (!result.ok) return result;
    if (!Array.isArray(result.data?.Libraries)) {
        return { ok: false, status: result.status, error: "Unexpected storage response shape" };
    }
    return { ok: true, status: result.status, data: result.data.Libraries };
}

// Database maintenance. Without force the server answers 409 when the existing
// database cannot be read for backup; forcing discards it and rebuilds empty.
export function rebuildDatabase(
    options?: { forceCleanOnBackupFailure: boolean },
    signal?: AbortSignal,
): Promise<ApiResult<null>> {
    const query = options?.forceCleanOnBackupFailure ? "?forceCleanOnBackupFailure=true" : "";
    return request<null>("Intros/RebuildDatabase" + query, { method: "POST", signal });
}

export function resetConfiguration(signal?: AbortSignal): Promise<ApiResult<null>> {
    return request<null>("IntroSkipper/Configuration/Reset", { method: "POST", signal });
}

// Skip button web patch helpers.
export function injectSkipButtonCss(signal?: AbortSignal): Promise<ApiResult<null>> {
    return request<null>("SkipButtonCss/InjectCss", { method: "POST", signal });
}

export function updateSkipDuration(signal?: AbortSignal): Promise<ApiResult<null>> {
    return request<null>("SkipButtonCss/UpdateSkipDuration", { method: "POST", signal });
}

// Plugin discovery.
export async function checkPlugins(signal?: AbortSignal): Promise<ApiResult<PluginInfo[]>> {
    const result = await request<PluginInfo[]>("Plugins", { signal });
    if (result.ok && !Array.isArray(result.data)) {
        return { ok: false, status: result.status, error: "Unexpected plugins response shape" };
    }
    return result;
}
