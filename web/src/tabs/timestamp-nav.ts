import type { ShowItem } from "../types.ts";
import * as tsData from "./timestamp-data.ts";

// Navigation state discriminated union.
export type NavState =
    | { view: "libraries" }
    | { view: "shows"; libraryId: string; libraryName: string }
    | { view: "episodes"; show: ShowItem; seasonId: string; seasonName: string };

/**
 * Manages navigation state, staleness guards, loading indicators,
 * and the per-library show cache for the timestamps browser.
 */
export function createNavState() {
    let destroyed = false;
    let viewVersion = 0;
    let panelVersion = 0;
    let loadingDepth = 0;
    let state: NavState = { view: "libraries" };

    const libraryShows = new Map<string, ShowItem[]>();
    const libraryLoaders = new Map<string, Promise<ShowItem[]>>();

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
     * Returns shows for a library, fetching and caching on first access.
     * Deduplicates in-flight requests for the same library.
     *
     * @param onCount Called with the formatted item count when available.
     * @param onError Called when the fetch fails.
     */
    function ensureLibraryShows(
        libraryId: string,
        libraryName: string,
        onCount?: (count: string) => void,
        onError?: () => void,
    ): Promise<ShowItem[]> {
        const cached = libraryShows.get(libraryId);
        if (cached) {
            onCount?.(formatItemCount(cached.length));
            return Promise.resolve(cached);
        }

        const inFlight = libraryLoaders.get(libraryId);
        if (inFlight) return inFlight;

        const loadPromise = tsData
            .getShowsInLibrary(libraryId, libraryName)
            .then((shows: ShowItem[]) => {
                if (!isAlive()) return [];
                libraryShows.set(libraryId, shows);
                onCount?.(formatItemCount(shows.length));
                return shows;
            })
            .catch((err) => {
                if (isAlive()) onError?.();
                throw err;
            })
            .finally(() => {
                libraryLoaders.delete(libraryId);
            });

        libraryLoaders.set(libraryId, loadPromise);
        return loadPromise;
    }

    function getCachedShows(libraryId: string): ShowItem[] | undefined {
        return libraryShows.get(libraryId);
    }

    function getAllCachedShows(): ShowItem[] {
        return Array.from(libraryShows.values()).reduce<ShowItem[]>(
            (allShows, shows) => {
                allShows.push(...shows);
                return allShows;
            },
            [],
        );
    }

    function formatItemCount(count: number): string {
        return count === 1 ? "1 item" : count + " items";
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
