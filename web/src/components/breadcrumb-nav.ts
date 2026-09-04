import { el } from "./dom.ts";
import type { ShowItem } from "../types.ts";

/** Delay before executing a search query (ms). */
const SEARCH_DEBOUNCE_MS = 150;

let searchListCounter = 0;

export type BreadcrumbSegment = {
    label: string;
    onClick?: () => void;
};


type SearchOption = {
    id: string;
    show: ShowItem;
    element: HTMLElement;
};

type BreadcrumbNavOptions = {
    segments: BreadcrumbSegment[];
    allShows: ShowItem[];
    onSearchSelect: (show: ShowItem) => void;
};

export function breadcrumbNav(opts: BreadcrumbNavOptions): {
    container: HTMLElement;
    updateSegments: (segments: BreadcrumbSegment[]) => void;
    updateShows: (shows: ShowItem[]) => void;
    destroy: () => void;
} {
    const container = el("div", { className: "ts-top-bar" });
    const crumbsNav = el("nav", { className: "ts-breadcrumbs-nav", "aria-label": "Breadcrumb" });
    const crumbsEl = el("ol", { className: "ts-breadcrumbs" });
    crumbsNav.append(crumbsEl);

    const searchWrapper = el("div", { className: "ts-search-wrapper" });
    const searchIcon = el("span", { className: "ts-search-icon" }, "\u2315");
    searchIcon.setAttribute("aria-hidden", "true");
    const searchInput = el("input", {
        className: "ts-search-input",
        type: "search",
        placeholder: "Search all shows\u2026",
    }) as HTMLInputElement;
    searchInput.setAttribute("aria-label", "Search all shows");
    searchInput.setAttribute("autocomplete", "off");
    searchInput.setAttribute("name", "show-search");
    searchInput.setAttribute("role", "combobox");
    searchInput.setAttribute("aria-autocomplete", "list");
    searchInput.setAttribute("aria-haspopup", "listbox");

    const resultsEl = el("div", { className: "ts-search-results" });
    resultsEl.id = "ts-show-search-results-" + String(++searchListCounter);
    resultsEl.setAttribute("role", "listbox");
    resultsEl.setAttribute("aria-label", "Search results");
    searchInput.setAttribute("aria-controls", resultsEl.id);
    searchInput.setAttribute("aria-expanded", "false");

    searchWrapper.append(searchIcon, searchInput, resultsEl);
    container.append(crumbsNav, searchWrapper);

    let currentShows: ShowItem[] = opts.allShows;
    let currentResults: SearchOption[] = [];
    let activeResultIndex = -1;

    function resetActiveResult(): void {
        activeResultIndex = -1;
        searchInput.removeAttribute("aria-activedescendant");
        for (const option of currentResults) {
            option.element.classList.remove("active");
            option.element.setAttribute("aria-selected", "false");
        }
    }

    function closeResults(): void {
        resultsEl.classList.remove("open");
        searchInput.setAttribute("aria-expanded", "false");
        resetActiveResult();
    }

    function openResults(): void {
        resultsEl.classList.add("open");
        searchInput.setAttribute("aria-expanded", "true");
    }

    function setActiveResult(index: number): void {
        if (currentResults.length === 0) {
            resetActiveResult();
            return;
        }

        activeResultIndex = Math.max(0, Math.min(index, currentResults.length - 1));
        for (let i = 0; i < currentResults.length; i++) {
            const isActive = i === activeResultIndex;
            currentResults[i].element.classList.toggle("active", isActive);
            currentResults[i].element.setAttribute("aria-selected", String(isActive));
        }

        const activeResult = currentResults[activeResultIndex];
        searchInput.setAttribute("aria-activedescendant", activeResult.id);
        activeResult.element.scrollIntoView({ block: "nearest" });
    }

    function selectResult(show: ShowItem): void {
        searchInput.value = "";
        closeResults();
        opts.onSearchSelect(show);
    }

    function renderSegments(segments: BreadcrumbSegment[]): void {
        crumbsEl.replaceChildren();

        segments.forEach((seg, i) => {
            const item = el("li", { className: "ts-breadcrumb-item" });
            if (i > 0) {
                item.append(el("span", { className: "ts-breadcrumb-sep" }, "\u203A"));
            }

            const isLast = i === segments.length - 1;
            if (isLast || !seg.onClick) {
                const span = el("span", { className: "ts-breadcrumb-current" }, seg.label);
                if (isLast) {
                    span.setAttribute("aria-current", "page");
                }
                item.append(span);
            } else {
                const link = el(
                    "button",
                    { className: "ts-breadcrumb-link", type: "button" },
                    seg.label,
                );
                link.addEventListener("click", () => seg.onClick?.());
                item.append(link);
            }

            crumbsEl.append(item);
        });
    }

    function appendSearchOption(show: ShowItem, label: string): void {
        const optionId = "ts-search-result-" + String(currentResults.length + 1);
        const item = el("div", { className: "ts-search-result", id: optionId }, label);
        item.setAttribute("role", "option");
        item.setAttribute("aria-selected", "false");

        const optionIndex = currentResults.length;
        item.addEventListener("mousemove", () => {
            setActiveResult(optionIndex);
        });
        item.addEventListener("mousedown", (event) => {
            event.preventDefault();
        });
        item.addEventListener("click", () => {
            selectResult(show);
        });

        currentResults.push({ id: optionId, show, element: item });
        resultsEl.append(item);
    }

    function renderSearchResults(query: string): void {
        resultsEl.replaceChildren();
        currentResults = [];
        resetActiveResult();

        if (!query.trim()) {
            closeResults();
            return;
        }

        const lower = query.toLowerCase();
        const matches = currentShows.filter((show) => show.Name.toLowerCase().includes(lower));

        if (matches.length === 0) {
            resultsEl.append(
                el("div", { className: "ts-search-empty", role: "status" }, "No shows found."),
            );
            openResults();
            return;
        }

        // Group results by library so duplicate show names stay distinguishable.
        const grouped: Record<string, ShowItem[]> = {};
        for (const match of matches) {
            const libraryName = match.LibraryName;
            if (!grouped[libraryName]) grouped[libraryName] = [];
            grouped[libraryName].push(match);
        }

        for (const libraryName of Object.keys(grouped)) {
            resultsEl.append(el("div", { className: "ts-search-group-label" }, libraryName));
            for (const show of grouped[libraryName]) {
                const yearStr = show.ProductionYear ? " (" + show.ProductionYear + ")" : "";
                appendSearchOption(show, show.Name + yearStr);
            }
        }

        openResults();
        setActiveResult(0);
    }

    let debounceTimer: ReturnType<typeof setTimeout> | null = null;
    const handleInput = () => {
        if (debounceTimer) clearTimeout(debounceTimer);
        debounceTimer = setTimeout(() => {
            renderSearchResults(searchInput.value);
        }, SEARCH_DEBOUNCE_MS);
    };

    searchInput.addEventListener("input", handleInput);

    const handleKeydown = (event: KeyboardEvent) => {
        if (event.key === "ArrowDown") {
            if (!searchInput.value.trim()) return;
            if (!resultsEl.classList.contains("open")) {
                renderSearchResults(searchInput.value);
            }
            if (currentResults.length > 0) {
                event.preventDefault();
                setActiveResult(activeResultIndex < 0 ? 0 : activeResultIndex + 1);
            }
        } else if (event.key === "ArrowUp") {
            if (!resultsEl.classList.contains("open") || currentResults.length === 0) return;
            event.preventDefault();
            setActiveResult(activeResultIndex - 1);
        } else if (event.key === "Home") {
            if (!resultsEl.classList.contains("open") || currentResults.length === 0) return;
            event.preventDefault();
            setActiveResult(0);
        } else if (event.key === "End") {
            if (!resultsEl.classList.contains("open") || currentResults.length === 0) return;
            event.preventDefault();
            setActiveResult(currentResults.length - 1);
        } else if (event.key === "Enter") {
            if (!resultsEl.classList.contains("open") || activeResultIndex < 0) return;
            event.preventDefault();
            selectResult(currentResults[activeResultIndex].show);
        } else if (event.key === "Escape") {
            closeResults();
        }
    };

    const handleFocusout = (event: FocusEvent) => {
        const nextTarget = event.relatedTarget;
        if (!(nextTarget instanceof Node) || !searchWrapper.contains(nextTarget)) {
            closeResults();
        }
    };

    const handleFocus = () => {
        if (searchInput.value.trim()) {
            renderSearchResults(searchInput.value);
        }
    };

    searchInput.addEventListener("keydown", handleKeydown);
    searchWrapper.addEventListener("focusout", handleFocusout);
    searchInput.addEventListener("focus", handleFocus);

    renderSegments(opts.segments);

    return {
        container,
        updateSegments(segments: BreadcrumbSegment[]) {
            renderSegments(segments);
        },
        updateShows(shows: ShowItem[]) {
            currentShows = shows;
            if (searchInput.value.trim()) {
                renderSearchResults(searchInput.value);
            }
        },

        destroy() {
            if (debounceTimer) {
                clearTimeout(debounceTimer);
                debounceTimer = null;
            }
            searchInput.removeEventListener("input", handleInput);
            searchInput.removeEventListener("keydown", handleKeydown);
            searchInput.removeEventListener("focus", handleFocus);
            searchWrapper.removeEventListener("focusout", handleFocusout);
            closeResults();
        },
    };
}
