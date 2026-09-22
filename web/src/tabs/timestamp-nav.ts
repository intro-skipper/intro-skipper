import type { ShowItem } from "../types.ts";
import { getShowsInLibrary } from "../store/jellyfin-client.ts";
import { abortable } from "../lifecycle.ts";

// Navigation state discriminated union.
type NavState =
    | { view: "libraries" }
    | { view: "shows"; libraryId: string; libraryName: string }
    | { view: "episodes"; show: ShowItem; seasonId: string; seasonName: string };

/**
 * Navigation state, the dashboard loading indicator's depth count, and the
 * resolved per-library show lists for the timestamps browser. The loading
 * indicator is cleared when `signal` aborts.
 */
export function createNavState(signal: AbortSignal) {
    let loadingDepth = 0;
    let state: NavState = { view: "libraries" };

    // Resolved show lists, for synchronous reads (search index, cached views).
    // The fetch itself is deduplicated and cached by jellyfin-client.
    const libraryShows = new Map<string, ShowItem[]>();

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

    signal.addEventListener(
        "abort",
        () => {
            if (loadingDepth === 0) return;
            loadingDepth = 0;
            window.Dashboard.hideLoadingMsg();
        },
        { once: true },
    );

    /**
     * Loads a library's shows and records them for synchronous access. The
     * record feeds the tab-wide search index, so it lives for the tab, not the
     * view that asked: a caller rendering a view wraps the call in abortable()
     * with its own signal.
     */
    async function ensureLibraryShows(libraryId: string, libraryName: string): Promise<ShowItem[]> {
        const shows = await abortable(getShowsInLibrary(libraryId, libraryName), signal);
        libraryShows.set(libraryId, shows);
        return shows;
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

    return {
        getState,
        setState,
        showDashboardLoading,
        hideDashboardLoading,
        ensureLibraryShows,
        getCachedShows,
        getAllCachedShows,
    };
}
