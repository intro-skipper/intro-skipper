import type { ShowItem, SeasonItem, EpisodeItem } from "../types.ts";
import { createNavState } from "./timestamp-nav.ts";
import * as tsData from "./timestamp-data.ts";
import { el } from "../components/dom.ts";
import { breadcrumbNav, type BreadcrumbSegment } from "../components/breadcrumb-nav.ts";
import { seasonTabs } from "../components/season-tabs.ts";
import { episodeList } from "../components/episode-list.ts";
import { actionBar } from "../components/action-bar.ts";
import { clickableCard } from "../components/clickable-card.ts";
import { createManageBar } from "../components/manage-bar.ts";

export function createTimestampsBrowser(container: HTMLElement): { destroy: () => void } {
    const nav$ = createNavState();
    let currentSeasonTabs: ReturnType<typeof seasonTabs> | null = null;

    const contentEl = el("div");
    const libraryCountEls = new Map<string, HTMLElement>();

    const nav = breadcrumbNav({
        segments: [{ label: "All Libraries" }],
        allShows: [],
        onSearchSelect: (show) => {
            void navigateToShow(show).catch(console.error);
        },
    });

    const epList = episodeList();
    const actions = actionBar({
        onScanComplete: () => refreshUnlessEditing(),
    });

    const panelEl = el("section", { className: "ts-season-panel", id: "timestamps-season-panel" });
    panelEl.tabIndex = -1;
    panelEl.append(actions.container, epList.container);

    container.append(nav.container, contentEl);

    void navigateToLibraries().catch(console.error);

    function createStatusMessage(message: string, color?: string): HTMLElement {
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

    async function navigateToLibraries(): Promise<void> {
        const viewToken = nav$.nextViewVersion();

        nav$.setState({ view: "libraries" });
        libraryCountEls.clear();
        resetViewContent();
        updateBreadcrumbs();

        nav$.showDashboardLoading();
        try {
            const libraries = await tsData.getLibraries();
            if (!nav$.isCurrentView(viewToken)) return;

            for (const lib of libraries) {
                const countEl = el(
                    "span",
                    { className: "ts-episode-runtime" },
                    "Loading items\u2026",
                );
                libraryCountEls.set(lib.Id, countEl);

                const card = clickableCard({
                    title: lib.Name,
                    subtitle: "Loading items\u2026",
                    onClick: () => {
                        void navigateToShows(lib.Id, lib.Name).catch(console.error);
                    },
                });

                if (card.subtitleEl) card.subtitleEl.replaceWith(countEl);
                contentEl.append(card.container);
            }

            void Promise.all(
                libraries.map((lib) =>
                    nav$
                        .ensureLibraryShows(
                            lib.Id,
                            lib.Name,
                            (count) => {
                                setLibraryCount(lib.Id, count);
                                syncSearchIndex();
                            },
                            () => setLibraryCount(lib.Id, "Unavailable"),
                        )
                        .catch(() => []),
                ),
            ).catch(console.error);
        } catch (err) {
            if (!nav$.isCurrentView(viewToken)) return;
            contentEl.append(
                createStatusMessage(
                    "Failed to load libraries: " +
                        (err instanceof Error ? err.message : "Unknown error"),
                    "var(--is-error)",
                ),
            );
        } finally {
            nav$.hideDashboardLoading();
        }
    }

    async function navigateToShows(libraryId: string, libraryName: string): Promise<void> {
        const viewToken = nav$.nextViewVersion();

        nav$.setState({ view: "shows", libraryId, libraryName });
        resetViewContent();
        updateBreadcrumbs();

        let libShows = nav$.getCachedShows(libraryId);

        if (!libShows) {
            contentEl.append(createStatusMessage("Loading shows\u2026"));
            nav$.showDashboardLoading();
            try {
                libShows = await nav$.ensureLibraryShows(
                    libraryId,
                    libraryName,
                    (count) => setLibraryCount(libraryId, count),
                    () => setLibraryCount(libraryId, "Unavailable"),
                );
                syncSearchIndex();
                if (!nav$.isCurrentView(viewToken)) return;
                contentEl.replaceChildren();
            } catch (err) {
                if (!nav$.isCurrentView(viewToken)) return;
                contentEl.replaceChildren();
                contentEl.append(
                    createStatusMessage(
                        "Failed to load shows: " +
                            (err instanceof Error ? err.message : "Unknown error"),
                        "var(--is-error)",
                    ),
                );
                return;
            } finally {
                nav$.hideDashboardLoading();
            }
        }

        if (!nav$.isCurrentView(viewToken)) return;

        if (!libShows || libShows.length === 0) {
            contentEl.append(createStatusMessage("No shows found in this library."));
            return;
        }

        for (const show of libShows) {
            const yearStr = show.ProductionYear ? " (" + show.ProductionYear + ")" : "";
            const card = clickableCard({
                title: show.Name + yearStr,
                subtitle: show.Type,
                onClick: () => {
                    void navigateToShow(show).catch(console.error);
                },
            });
            contentEl.append(card.container);
        }
    }

    async function navigateToShow(show: ShowItem): Promise<void> {
        const viewToken = nav$.nextViewVersion();

        resetViewContent();

        if (show.Type === "Movie") {
            nav$.setState({ view: "episodes", show, seasonId: show.Id, seasonName: "" });
            updateBreadcrumbs();

            const { container: movieBar } = createManageBar({
                managePanelId: actions.container.id,
                onManageToggle: (open) => actions.toggle(open),
            });

            contentEl.append(movieBar, panelEl);
            await loadMovieEpisodes(show);
            return;
        }

        nav$.showDashboardLoading();
        try {
            const seasons = await tsData.getSeasons(show.Id);
            if (!nav$.isCurrentView(viewToken)) return;

            if (seasons.length === 0) {
                contentEl.append(createStatusMessage("No seasons found."));
                return;
            }

            const firstSeason = seasons[0];
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
                    void switchSeason(show, season).catch(console.error);
                },
                onManageToggle: (open) => actions.toggle(open),
            });

            setPanelTabState(currentSeasonTabs.getTabId(firstSeason.Id));
            contentEl.append(currentSeasonTabs.container, panelEl);

            await loadSeasonEpisodes(show, firstSeason);
        } catch (err) {
            if (!nav$.isCurrentView(viewToken)) return;
            contentEl.append(
                createStatusMessage(
                    "Failed to load seasons: " +
                        (err instanceof Error ? err.message : "Unknown error"),
                    "var(--is-error)",
                ),
            );
        } finally {
            nav$.hideDashboardLoading();
        }
    }

    async function switchSeason(show: ShowItem, season: SeasonItem): Promise<void> {
        if (!nav$.isAlive()) return;

        nav$.setState({ view: "episodes", show, seasonId: season.Id, seasonName: season.Name });
        setPanelTabState(currentSeasonTabs?.getTabId(season.Id) ?? null);
        updateBreadcrumbs();
        await loadSeasonEpisodes(show, season);
    }

    async function loadSeasonEpisodes(show: ShowItem, season: SeasonItem): Promise<void> {
        const panelToken = nav$.nextPanelVersion();

        setPanelBusy(true);
        epList.clear();
        epList.setStatus("Loading episodes\u2026");
        actions.toggle(false);

        nav$.showDashboardLoading();
        try {
            const { episodes, segments, disabledItemIds } = await tsData.getEpisodesWithSegments(
                show.Id,
                season.Id,
            );
            if (!nav$.isCurrentPanel(panelToken)) return;

            if (episodes.length === 0) {
                epList.setStatus("No episodes found.");
                return;
            }

            epList.render(episodes, segments, false, disableOption(disabledItemIds));
            warnDisableStateUnknown(
                disabledItemIds,
                "Failed to load media-segment settings; the enable/disable toggles are hidden.",
            );
            await actions.loadForSeason(show.Id, season.Id, false);
        } catch (err) {
            if (!nav$.isCurrentPanel(panelToken)) return;
            epList.setStatus(
                "Failed to load episodes: " +
                    (err instanceof Error ? err.message : "Unknown error"),
                "var(--is-error)",
            );
        } finally {
            if (nav$.isCurrentPanel(panelToken)) {
                setPanelBusy(false);
            }
            nav$.hideDashboardLoading();
        }
    }

    async function loadMovieEpisodes(show: ShowItem): Promise<void> {
        const panelToken = nav$.nextPanelVersion();

        setPanelBusy(true);
        epList.clear();
        epList.setStatus("Loading timestamps\u2026");
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
                tsData.getMovieSegments(show.Id),
                // A movie's season-state key is its own ID.
                tsData.getDisabledItemIds(show.Id),
            ]);
            if (!nav$.isCurrentPanel(panelToken)) return;

            epList.render([movieEp], [result], true, disableOption(disabledItemIds));
            warnDisableStateUnknown(
                disabledItemIds,
                "Failed to load media-segment settings; the enable/disable toggle is hidden.",
            );
            await actions.loadForSeason(show.Id, show.Id, true);
        } catch (err) {
            if (!nav$.isCurrentPanel(panelToken)) return;
            epList.setStatus(
                "Failed to load timestamps: " +
                    (err instanceof Error ? err.message : "Unknown error"),
                "var(--is-error)",
            );
        } finally {
            if (nav$.isCurrentPanel(panelToken)) {
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
        const result = await tsData.setItemDisabled(itemId, disabled);
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
                        void refreshEpisodes().catch(console.error);
                    },
                },
            );
            return;
        }
        await refreshEpisodes();
    }

    async function refreshEpisodes(): Promise<void> {
        const state = nav$.getState();
        if (!nav$.isAlive() || state.view !== "episodes") return;

        const { show, seasonId, seasonName } = state;
        if (show.Type === "Movie") {
            await loadMovieEpisodes(show);
            return;
        }

        const season: SeasonItem = { Id: seasonId, Name: seasonName, IndexNumber: null };
        await loadSeasonEpisodes(show, season);
    }

    function updateBreadcrumbs(): void {
        const state = nav$.getState();
        const segments: BreadcrumbSegment[] = [];

        segments.push({
            label: "All Libraries",
            onClick:
                state.view !== "libraries"
                    ? () => {
                          void navigateToLibraries().catch(console.error);
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
                              void navigateToShows(libId, libName).catch(console.error);
                          }
                        : undefined,
            });
        }

        if (state.view === "episodes") {
            const show = state.show;
            const yearStr = show.ProductionYear ? " (" + show.ProductionYear + ")" : "";

            segments.push({
                label: show.Name + yearStr,
                onClick:
                    show.Type !== "Movie"
                        ? () => {
                              void navigateToShow(show).catch(console.error);
                          }
                        : undefined,
            });

            if (show.Type !== "Movie" && state.seasonName) {
                segments.push({ label: state.seasonName });
            }
        }

        nav.updateSegments(segments);
    }

    return {
        destroy() {
            nav$.destroy();
            nav.destroy();
            epList.destroy();
            actions.destroy();
            setPanelBusy(false);
        },
    };
}
