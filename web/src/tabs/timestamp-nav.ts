import type { ShowItem } from "../types.ts";
import { getShowsInLibrary } from "../store/jellyfin-client.ts";
import { pluralize } from "../utils.ts";

// Navigation state discriminated union.
type NavState =
    | { view: "libraries" }
    | { view: "shows"; libraryId: string; libraryName: string }
    | { view: "episodes"; show: ShowItem; seasonId: string; seasonName: string };

/**
 * Manages navigation state, staleness guards, loading indicators,
 * and the resolved per-library show lists for the timestamps browser.
 */
export function createNavState() {
    let destroyed = false;
    let viewVersion = 0;
    let panelVersion = 0;
    let loadingDepth = 0;
    let state: NavState = { view: "libraries" };

    // Resolved show lists, for synchronous reads (search index, cached views).
    // The fetch itself is deduplicated and cached by jellyfin-client.
    const libraryShows = new Map<string, ShowItem[]>();

    function nextViewVersion(): number {
        viewVersion += 1;
        panelVersion += 1;
        return viewVersion;
    }

    function nextPanelVersion(): number {
        panelVersion += 1;
        return panelVersion;
    }

    function isCurrentView(version: number): boolean {
        return !destroyed && version === viewVersion;
    }

    function isCurrentPanel(version: number): boolean {
        return !destroyed && version === panelVersion;
    }

    function isAlive(): boolean {
        return !destroyed;
    }

    function showDashboardLoading(): void {
        if (loadingDepth === 0) {
            window.Dashboard.showLoadingMsg();
        }
        loadingDepth += 1;
    }

    function hideDashboardLoading(): void {
        if (loadingDepth === 0) return;
        loadingDepth -= 1;
        if (loadingDepth === 0) {
            window.Dashboard.hideLoadingMsg();
        }
    }

    function resetDashboardLoading(): void {
        if (loadingDepth === 0) return;
        loadingDepth = 0;
        window.Dashboard.hideLoadingMsg();
    }

    /**
     * Returns shows for a library and records them for synchronous access.
     *
     * @param onCount Called with the formatted item count when available.
     * @param onError Called when the fetch fails.
     */
    async function ensureLibraryShows(
        libraryId: string,
        libraryName: string,
        onCount?: (count: string) => void,
        onError?: () => void,
    ): Promise<ShowItem[]> {
        try {
            const shows = await getShowsInLibrary(libraryId, libraryName);
            if (!isAlive()) return [];
            libraryShows.set(libraryId, shows);
            onCount?.(pluralize(shows.length, "item"));
            return shows;
        } catch (err) {
            if (isAlive()) onError?.();
            throw err;
        }
    }

    function getCachedShows(libraryId: string): ShowItem[] | undefined {
        return libraryShows.get(libraryId);
    }

    function getAllCachedShows(): ShowItem[] {
        return Array.from(libraryShows.values()).flat();
    }

    function getState(): NavState {
        return state;
    }

    function setState(next: NavState): void {
        state = next;
    }

    function destroy(): void {
        destroyed = true;
        viewVersion += 1;
        panelVersion += 1;
        resetDashboardLoading();
    }

    return {
        // State
        getState,
        setState,
        destroy,

        // Version guards
        nextViewVersion,
        nextPanelVersion,
        isCurrentView,
        isCurrentPanel,
        isAlive,

        // Loading
        showDashboardLoading,
        hideDashboardLoading,

        // Library cache
        ensureLibraryShows,
        getCachedShows,
        getAllCachedShows,
    };
}
