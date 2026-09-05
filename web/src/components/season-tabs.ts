import { el } from "./dom.ts";
import { appendManageToggle } from "./manage-bar.ts";
import type { SeasonItem } from "../types.ts";

let seasonTabsCounter = 0;

type SeasonTabsOptions = {
    seasons: SeasonItem[];
    activeSeasonId: string | null;
    panelId?: string;
    managePanelId?: string;
    onSeasonSelect: (season: SeasonItem) => void;
    onManageToggle: (open: boolean) => void;
};

export function seasonTabs(opts: SeasonTabsOptions): {
    container: HTMLElement;
    setActive: (seasonId: string) => void;
    getTabId: (seasonId: string) => string | null;
} {
    const container = el("div", { className: "ts-season-bar" });
    container.setAttribute("role", "tablist");
    container.setAttribute("aria-label", "Seasons");

    const instanceId = ++seasonTabsCounter;
    let orderedSeasons: SeasonItem[] = [];

    // Cache buttons so changing seasons only updates active state.
    const tabButtons = new Map<string, HTMLButtonElement>();
    const tabIds = new Map<string, string>();

    function getTabId(seasonId: string): string | null {
        return tabIds.get(seasonId) ?? null;
    }

    function updateActive(newActiveId: string | null): void {
        for (const [id, btn] of tabButtons) {
            const isActive = id === newActiveId;
            btn.classList.toggle("active", isActive);
            btn.setAttribute("aria-selected", String(isActive));
            btn.tabIndex = isActive ? 0 : -1;
        }
    }

    function activateSeason(season: SeasonItem, moveFocus = false): void {
        updateActive(season.Id);
        if (moveFocus) {
            tabButtons.get(season.Id)?.focus();
        }
        opts.onSeasonSelect(season);
    }

    function moveByOffset(currentSeasonId: string, offset: number): void {
        const currentIndex = orderedSeasons.findIndex((season) => season.Id === currentSeasonId);
        if (currentIndex === -1 || orderedSeasons.length === 0) return;

        const nextIndex = (currentIndex + offset + orderedSeasons.length) % orderedSeasons.length;
        activateSeason(orderedSeasons[nextIndex], true);
    }

    function moveToIndex(index: number): void {
        if (orderedSeasons.length === 0) return;
        const nextIndex = Math.max(0, Math.min(index, orderedSeasons.length - 1));
        activateSeason(orderedSeasons[nextIndex], true);
    }

    function handleTabKeydown(event: KeyboardEvent, season: SeasonItem): void {
        if (event.key === "ArrowRight" || event.key === "ArrowDown") {
            event.preventDefault();
            moveByOffset(season.Id, 1);
        } else if (event.key === "ArrowLeft" || event.key === "ArrowUp") {
            event.preventDefault();
            moveByOffset(season.Id, -1);
        } else if (event.key === "Home") {
            event.preventDefault();
            moveToIndex(0);
        } else if (event.key === "End") {
            event.preventDefault();
            moveToIndex(orderedSeasons.length - 1);
        }
    }

    function render(seasons: SeasonItem[], activeId: string | null): void {
        orderedSeasons = seasons;
        container.replaceChildren();
        tabButtons.clear();
        tabIds.clear();

        for (let index = 0; index < seasons.length; index++) {
            const season = seasons[index];
            const label = season.IndexNumber != null ? "S" + season.IndexNumber : season.Name;
            const isActive = season.Id === activeId;
            const tabId = "ts-season-tab-" + instanceId + "-" + String(index + 1);
            const tab = el(
                "button",
                {
                    className: "ts-season-tab" + (isActive ? " active" : ""),
                    id: tabId,
                    type: "button",
                },
                label,
            );

            tab.title = season.Name;
            tab.setAttribute("role", "tab");
            tab.setAttribute("aria-selected", String(isActive));
            tab.tabIndex = isActive ? 0 : -1;
            if (opts.panelId) {
                tab.setAttribute("aria-controls", opts.panelId);
            }

            tab.addEventListener("click", () => {
                activateSeason(season);
            });
            tab.addEventListener("keydown", (event) => {
                handleTabKeydown(event, season);
            });

            tabButtons.set(season.Id, tab);
            tabIds.set(season.Id, tabId);
            container.append(tab);
        }

        appendManageToggle(container, {
            managePanelId: opts.managePanelId,
            onManageToggle: opts.onManageToggle,
        });
    }

    render(opts.seasons, opts.activeSeasonId);

    return {
        container,
        setActive(seasonId: string) {
            updateActive(seasonId);
        },
        getTabId,
    };
}
