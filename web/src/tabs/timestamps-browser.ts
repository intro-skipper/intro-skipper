import type { LibraryInfo, ShowItem, SeasonItem, EpisodeItem, Tab } from "../types.ts";
import { createNavState } from "./timestamp-nav.ts";
import { getEpisodesWithSegments, getDisabledItemIds } from "./timestamp-data.ts";
import { getLibraries, getSeasons } from "../store/jellyfin-client.ts";
import * as api from "../store/api.ts";
import { el } from "../components/dom.ts";
import { errorText, pluralize, showTitle } from "../utils.ts";
import { abortable, childScope, ignoreAbort, isAbortError } from "../lifecycle.ts";
import { breadcrumbNav, type BreadcrumbSegment } from "../components/breadcrumb-nav.ts";
import { seasonTabs } from "../components/season-tabs.ts";
import { episodeList } from "../components/episode-list.ts";
import { actionBar } from "../components/action-bar.ts";
import { clickableCard } from "../components/clickable-card.ts";
import { appendManageToggle } from "../components/manage-bar.ts";

export const timestampsTab: Tab = {
    id: "timestamps",
    label: "Timestamps",
    render(container, signal) {
        createTimestampsBrowser(container, signal);
    },
};

/**
 * Three nested lifetimes: the tab (`signal`), the current view (all libraries,
 * one library's shows, or one show's episodes) and, inside an episodes view,
 * the current season panel. Navigating aborts the view scope; switching seasons
 * aborts the panel scope. Requests take the innermost signal, so a stale
 * continuation stops at its next await instead of drawing into the new view.
 */
function createTimestampsBrowser(container: HTMLElement, signal: AbortSignal): void {
    const nav$ = createNavState(signal);
    let viewScope = childScope(signal);
    let panelScope = childScope(viewScope.signal);
    let currentSeasonTabs: ReturnType<typeof seasonTabs> | null = null;
    let currentSeasons: SeasonItem[] = [];

    const contentEl = el("div");
    const libraryCountEls = new Map<string, HTMLElement>();

    const nav = breadcrumbNav({
        segments: [{ label: "All Libraries" }],
        allShows: [],
        onSearchSelect: (show) => {
            void navigateToShow(show).catch(ignoreAbort);
        },
        signal,
    });

    const epList = episodeList(signal);
    const actions = actionBar({
        onScanComplete: () => refreshUnlessEditing(),
        signal,
    });

    const panelEl = el("section", { className: "ts-season-panel", id: "timestamps-season-panel" });
    panelEl.tabIndex = -1;
    panelEl.append(actions.container, epList.container);

    container.append(nav.container, contentEl);

    void navigateToLibraries().catch(ignoreAbort);

    function nextView(): AbortSignal {
        viewScope.abort();
        viewScope = childScope(signal);
        panelScope = childScope(viewScope.signal);
        return viewScope.signal;
    }

    function nextPanel(): AbortSignal {
        panelScope.abort();
        panelScope = childScope(viewScope.signal);
        return panelScope.signal;
    }

    function statusLine(message: string, color?: string): HTMLElement {
        const attrs: Record<string, string> = { className: "ts-status-msg" };
        if (color) {
            attrs.style = "color: " + color;
        }
        return el("div", attrs, message);
    }

    function setPanelTabState(tabId: string | null): void {
        if (tabId) {
            panelEl.setAttribute("role", "tabpanel");
            panelEl.setAttribute("aria-labelledby", tabId);
        } else {
            panelEl.removeAttribute("role");
            panelEl.removeAttribute("aria-labelledby");
        }
    }

    function setPanelBusy(isBusy: boolean): void {
        panelEl.setAttribute("aria-busy", String(isBusy));
    }

    function resetViewContent(): void {
        currentSeasonTabs = null;
        currentSeasons = [];
        contentEl.replaceChildren();
        setPanelBusy(false);
        setPanelTabState(null);
        actions.toggle(false);
    }

    function syncSearchIndex(): void {
        nav.updateShows(nav$.getAllCachedShows());
    }

    function setLibraryCount(libraryId: string, text: string): void {
        const countEl = libraryCountEls.get(libraryId);
        if (countEl) {
            countEl.textContent = text;
        }
    }

    // The listing feeds the tab-wide search index even after the libraries view
    // is gone; only the card's count belongs to the view.
    async function loadLibraryCount(lib: LibraryInfo, view: AbortSignal): Promise<void> {
        try {
            const shows = await nav$.ensureLibraryShows(lib.Id, lib.Name);
            syncSearchIndex();
            if (!view.aborted) setLibraryCount(lib.Id, pluralize(shows.length, "item"));
        } catch (err) {
            if (isAbortError(err)) return;
            if (!view.aborted) setLibraryCount(lib.Id, "Unavailable");
        }
    }

    async function navigateToLibraries(): Promise<void> {
        const view = nextView();

        nav$.setState({ view: "libraries" });
        libraryCountEls.clear();
        resetViewContent();
        updateBreadcrumbs();

        nav$.showDashboardLoading();
        try {
            const libraries = await abortable(getLibraries(), view);

            for (const lib of libraries) {
                const countEl = el(
                    "span",
                    { className: "ts-episode-runtime" },
                    "Loading items…",
                );
                libraryCountEls.set(lib.Id, countEl);

                const card = clickableCard({
                    title: lib.Name,
                    subtitle: countEl,
                    onClick: () => {
                        void navigateToShows(lib.Id, lib.Name).catch(ignoreAbort);
                    },
                });

                contentEl.append(card);
            }

            void Promise.all(libraries.map((lib) => loadLibraryCount(lib, view)));
        } catch (err) {
            if (isAbortError(err)) throw err;
            contentEl.append(
                statusLine("Failed to load libraries: " + errorText(err), "var(--is-error)"),
            );
        } finally {
            nav$.hideDashboardLoading();
        }
    }

    async function navigateToShows(libraryId: string, libraryName: string): Promise<void> {
        const view = nextView();

        nav$.setState({ view: "shows", libraryId, libraryName });
        resetViewContent();
        updateBreadcrumbs();

        let libShows = nav$.getCachedShows(libraryId);

        if (!libShows) {
            contentEl.append(statusLine("Loading shows…"));
            nav$.showDashboardLoading();
            try {
                libShows = await abortable(nav$.ensureLibraryShows(libraryId, libraryName), view);
                setLibraryCount(libraryId, pluralize(libShows.length, "item"));
                syncSearchIndex();
                contentEl.replaceChildren();
            } catch (err) {
                if (isAbortError(err)) throw err;
                setLibraryCount(libraryId, "Unavailable");
                contentEl.replaceChildren();
                contentEl.append(
                    statusLine("Failed to load shows: " + errorText(err), "var(--is-error)"),
                );
                return;
            } finally {
                nav$.hideDashboardLoading();
            }
        }

        if (libShows.length === 0) {
            contentEl.append(statusLine("No shows found in this library."));
            return;
        }

        for (const show of libShows) {
            const card = clickableCard({
                title: showTitle(show),
                subtitle: show.Type,
                onClick: () => {
                    void navigateToShow(show).catch(ignoreAbort);
                },
            });
            contentEl.append(card);
        }
    }

    async function navigateToShow(show: ShowItem): Promise<void> {
        const view = nextView();

        resetViewContent();

        if (show.Type === "Movie") {
            nav$.setState({ view: "episodes", show, seasonId: show.Id, seasonName: "" });
            updateBreadcrumbs();

            const movieBar = el("div", { className: "ts-season-bar" });
            appendManageToggle(movieBar, {
                managePanelId: actions.container.id,
                onManageToggle: (open) => actions.toggle(open),
            });

            contentEl.append(movieBar, panelEl);
            await loadMovieEpisodes(show);
            return;
        }

        nav$.showDashboardLoading();
        try {
            const seasons = await getSeasons(show.Id, view);

            if (seasons.length === 0) {
                contentEl.append(statusLine("No seasons found."));
                return;
            }

            const firstSeason = seasons[0];
            currentSeasons = seasons;
            nav$.setState({
                view: "episodes",
                show,
                seasonId: firstSeason.Id,
                seasonName: firstSeason.Name,
            });
            updateBreadcrumbs();

            currentSeasonTabs = seasonTabs({
                seasons,
                activeSeasonId: firstSeason.Id,
                panelId: panelEl.id,
                managePanelId: actions.container.id,
                onSeasonSelect: (season) => {
                    void switchSeason(show, season).catch(ignoreAbort);
                },
                onManageToggle: (open) => actions.toggle(open),
            });

            setPanelTabState(currentSeasonTabs.getTabId(firstSeason.Id));
            contentEl.append(currentSeasonTabs.container, panelEl);

            await loadSeasonEpisodes(show, firstSeason, seasons);
        } catch (err) {
            if (isAbortError(err)) throw err;
            contentEl.append(
                statusLine("Failed to load seasons: " + errorText(err), "var(--is-error)"),
            );
        } finally {
            nav$.hideDashboardLoading();
        }
    }

    async function switchSeason(show: ShowItem, season: SeasonItem): Promise<void> {
        if (signal.aborted) return;

        nav$.setState({ view: "episodes", show, seasonId: season.Id, seasonName: season.Name });
        setPanelTabState(currentSeasonTabs?.getTabId(season.Id) ?? null);
        updateBreadcrumbs();
        await loadSeasonEpisodes(show, season, currentSeasons);
    }

    async function loadSeasonEpisodes(
        show: ShowItem,
        season: SeasonItem,
        seriesSeasons: readonly SeasonItem[] = currentSeasons,
    ): Promise<void> {
        const panel = nextPanel();

        setPanelBusy(true);
        epList.clear();
        epList.setStatus("Loading episodes…");
        actions.toggle(false);

        nav$.showDashboardLoading();
        try {
            const { episodes, segments, disabledItemIds } = await getEpisodesWithSegments(
                show.Id,
                season.Id,
                panel,
            );

            if (episodes.length === 0) {
                epList.setStatus("No episodes found.");
                return;
            }

            epList.render({
                episodes,
                segments,
                disable: disableOption(disabledItemIds),
                signal: panel,
            });
            warnDisableStateUnknown(
                disabledItemIds,
                "Failed to load media-segment settings; the enable/disable toggles are hidden.",
            );
            await actions.loadForSeason(show.Id, season.Id, false, panel, seriesSeasons);
        } catch (err) {
            if (isAbortError(err)) throw err;
            epList.setStatus("Failed to load episodes: " + errorText(err), "var(--is-error)");
        } finally {
            if (!panel.aborted) {
                setPanelBusy(false);
            }
            nav$.hideDashboardLoading();
        }
    }

    async function loadMovieEpisodes(show: ShowItem): Promise<void> {
        const panel = nextPanel();

        setPanelBusy(true);
        epList.clear();
        epList.setStatus("Loading timestamps…");
        actions.toggle(false);

        nav$.showDashboardLoading();
        try {
            const movieEp: EpisodeItem = {
                Id: show.Id,
                Name: show.Name,
                IndexNumber: null,
                RunTimeTicks: null,
                SeriesName: null,
            };

            const [result, disabledItemIds] = await Promise.all([
                api.getEpisodeSegments(show.Id, panel),
                // A movie's season-state key is its own ID.
                getDisabledItemIds(show.Id, panel),
            ]);

            epList.render({
                episodes: [movieEp],
                segments: [result],
                isMovie: true,
                disable: disableOption(disabledItemIds),
                signal: panel,
            });
            warnDisableStateUnknown(
                disabledItemIds,
                "Failed to load media-segment settings; the enable/disable toggle is hidden.",
            );
            await actions.loadForSeason(show.Id, show.Id, true, panel);
        } catch (err) {
            if (isAbortError(err)) throw err;
            epList.setStatus("Failed to load timestamps: " + errorText(err), "var(--is-error)");
        } finally {
            if (!panel.aborted) {
                setPanelBusy(false);
            }
            nav$.hideDashboardLoading();
        }
    }

    // Maps the fetched disabled ids to the episode list's disable option. Null
    // (state unknown) hides the toggles rather than rendering a fabricated
    // all-enabled state; warnDisableStateUnknown surfaces the failure after render.
    function disableOption(
        disabledItemIds: string[] | null,
    ): { ids: string[]; onChange: (itemId: string, disabled: boolean) => Promise<void> } | undefined {
        return disabledItemIds === null
            ? undefined
            : { ids: disabledItemIds, onChange: updateItemDisabled };
    }

    function warnDisableStateUnknown(disabledItemIds: string[] | null, message: string): void {
        if (disabledItemIds === null) {
            epList.setStatus(message, "var(--is-error)");
        }
    }

    async function updateItemDisabled(itemId: string, disabled: boolean): Promise<void> {
        const result = await api.setItemDisabled(itemId, disabled);
        if (!result.ok) {
            // The toggle handler owns the user-facing message; this is only a signal.
            throw new Error("setItemDisabled failed");
        }
    }

    // Reloads the panel after a scan or erase changed the stored segments.
    // When an inline editor holds unsaved typed input, the reload is withheld
    // behind an explicit button so it cannot silently discard those edits.
    async function refreshUnlessEditing(): Promise<void> {
        if (epList.hasUnsavedEdits()) {
            epList.setStatus(
                "Results changed on the server. An editor has unsaved changes.",
                "var(--is-warning)",
                {
                    label: "Refresh",
                    onClick: () => {
                        void refreshEpisodes().catch(ignoreAbort);
                    },
                },
            );
            return;
        }
        await refreshEpisodes();
    }

    async function refreshEpisodes(): Promise<void> {
        const state = nav$.getState();
        if (signal.aborted || state.view !== "episodes") return;

        const { show, seasonId, seasonName } = state;
        if (show.Type === "Movie") {
            await loadMovieEpisodes(show);
            return;
        }

        const season: SeasonItem = { Id: seasonId, Name: seasonName, IndexNumber: null };
        await loadSeasonEpisodes(show, season, currentSeasons);
    }

    function updateBreadcrumbs(): void {
        const state = nav$.getState();
        const segments: BreadcrumbSegment[] = [];

        segments.push({
            label: "All Libraries",
            onClick:
                state.view !== "libraries"
                    ? () => {
                          void navigateToLibraries().catch(ignoreAbort);
                      }
                    : undefined,
        });

        if (state.view === "shows" || state.view === "episodes") {
            const libName = state.view === "shows" ? state.libraryName : state.show.LibraryName;
            const libId = state.view === "shows" ? state.libraryId : state.show.LibraryId;

            segments.push({
                label: libName,
                onClick:
                    state.view !== "shows"
                        ? () => {
                              void navigateToShows(libId, libName).catch(ignoreAbort);
                          }
                        : undefined,
            });
        }

        if (state.view === "episodes") {
            const show = state.show;
            segments.push({
                label: showTitle(show),
                onClick:
                    show.Type !== "Movie"
                        ? () => {
                              void navigateToShow(show).catch(ignoreAbort);
                          }
                        : undefined,
            });

            if (show.Type !== "Movie" && state.seasonName) {
                segments.push({ label: state.seasonName });
            }
        }

        nav.updateSegments(segments);
    }
}
