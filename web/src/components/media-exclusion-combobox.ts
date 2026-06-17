import type { PluginConfig, ShowItem } from "../types.ts";
import { formatConfiguredList, splitConfiguredList } from "../configured-list.ts";
import { configStore } from "../store/config-store.ts";
import { getExcludableMedia } from "../store/jellyfin-client.ts";
import { createStatusMessage } from "./async-feedback.ts";
import { el } from "./dom.ts";
import { appendFieldMeta } from "./field-meta.ts";

const MAX_RESULTS = 50;

let listCounter = 0;

type MediaType = "Series" | "Movie";

type MediaOption = ShowItem & { Type: MediaType };

type RenderedOption = {
    id: string;
    item: MediaOption;
    element: HTMLElement;
};

type ConfigChange = {
    field?: keyof PluginConfig;
};

function isSupportedMediaType(type: string): type is MediaType {
    return type === "Series" || type === "Movie";
}

function toMediaOptions(items: ShowItem[]): MediaOption[] {
    return items.filter((item): item is MediaOption => isSupportedMediaType(item.Type));
}

function optionLabel(item: MediaOption): string {
    const yearSuffix = item.ProductionYear == null ? "" : " (" + item.ProductionYear + ")";
    return item.Name + yearSuffix + " · " + item.Type + " · " + item.LibraryName;
}

function chipLabel(type: MediaType, name: string): string {
    return name + " · " + type;
}

export function mediaExclusionCombobox(): HTMLElement {
    const container = el("div", { className: "input-container media-exclusion-combobox" });
    const inputId = "field-media-exclusion";
    const resultsId = "media-exclusion-results-" + String(++listCounter);

    const label = el("label", { className: "input-label", for: inputId }, "Exclude series and movies");
    const describedByIds = appendFieldMeta(container, {
        idBase: inputId,
        description:
            "Select Jellyfin series and movies to exclude from analysis. Existing manual names remain selected even if Jellyfin no longer returns them.",
    });

    const control = el("div", { className: "media-exclusion-control" });
    const chipList = el("div", { className: "media-exclusion-chip-list" });
    const searchInput = el("input", {
        className: "media-exclusion-search",
        id: inputId,
        type: "search",
        role: "combobox",
        "aria-autocomplete": "list",
        "aria-haspopup": "listbox",
        "aria-expanded": "false",
        "aria-controls": resultsId,
        autocomplete: "off",
        name: "media-exclusion-search",
        placeholder: "Search series and movies…",
    }) as HTMLInputElement;
    if (describedByIds.length > 0) {
        searchInput.setAttribute("aria-describedby", describedByIds.join(" "));
    }

    const resultsEl = el("div", { className: "media-exclusion-results", id: resultsId });
    resultsEl.setAttribute("role", "listbox");
    resultsEl.setAttribute("aria-label", "Series and movie exclusion results");
    resultsEl.setAttribute("aria-multiselectable", "true");

    chipList.append(searchInput);
    control.append(chipList, resultsEl);

    const status = createStatusMessage({ className: "media-exclusion-notice", display: "block" });
    container.prepend(label);
    container.append(control, status.element);

    let mediaOptions: MediaOption[] = [];
    let selectedSeries = new Map<string, string>();
    let selectedMovies = new Map<string, string>();
    let renderedOptions: RenderedOption[] = [];
    let activeIndex = -1;
    let hasLoaded = false;
    let isLoading = false;
    let loadFailed = false;

    function rebuildSelectedFromConfig(): void {
        selectedSeries = new Map(
            splitConfiguredList(configStore.get("ExcludeSeries")).map((name) => [
                name.toLowerCase(),
                name,
            ]),
        );
        selectedMovies = new Map(
            splitConfiguredList(configStore.get("ExcludeMovies")).map((name) => [
                name.toLowerCase(),
                name,
            ]),
        );
    }

    function persistField(type: MediaType): void {
        if (type === "Series") {
            configStore.set("ExcludeSeries", formatConfiguredList(selectedSeries.values()));
        } else {
            configStore.set("ExcludeMovies", formatConfiguredList(selectedMovies.values()));
        }
    }

    function isSelected(type: MediaType, name: string): boolean {
        const key = name.toLowerCase();
        return type === "Series" ? selectedSeries.has(key) : selectedMovies.has(key);
    }

    function resetActiveOption(): void {
        activeIndex = -1;
        searchInput.removeAttribute("aria-activedescendant");
        for (const option of renderedOptions) {
            option.element.classList.remove("active");
        }
    }

    function closeResults(): void {
        resultsEl.classList.remove("open");
        searchInput.setAttribute("aria-expanded", "false");
        resetActiveOption();
    }

    function openResults(): void {
        resultsEl.classList.add("open");
        searchInput.setAttribute("aria-expanded", "true");
    }

    function setActiveOption(index: number): void {
        if (renderedOptions.length === 0) {
            resetActiveOption();
            return;
        }

        activeIndex = Math.max(0, Math.min(index, renderedOptions.length - 1));
        for (let i = 0; i < renderedOptions.length; i++) {
            const active = i === activeIndex;
            renderedOptions[i].element.classList.toggle("active", active);
        }

        const activeOption = renderedOptions[activeIndex];
        searchInput.setAttribute("aria-activedescendant", activeOption.id);
        activeOption.element.scrollIntoView({ block: "nearest" });
    }

    function renderChips(): void {
        const chips: HTMLElement[] = [];
        for (const name of selectedSeries.values()) {
            chips.push(createChip("Series", name));
        }
        for (const name of selectedMovies.values()) {
            chips.push(createChip("Movie", name));
        }

        chipList.replaceChildren(...chips, searchInput);
    }

    function createChip(type: MediaType, name: string): HTMLElement {
        const removeButton = el("button", { className: "media-exclusion-chip-remove", type: "button" }, "×");
        removeButton.setAttribute("aria-label", "Remove excluded " + type.toLowerCase() + " " + name);
        removeButton.addEventListener("click", () => {
            removeSelected(type, name);
            searchInput.focus();
        });

        return el("span", { className: "media-exclusion-chip" }, chipLabel(type, name), removeButton);
    }

    function addSelected(item: MediaOption): void {
        const target = item.Type === "Series" ? selectedSeries : selectedMovies;
        target.set(item.Name.toLowerCase(), item.Name);
    }

    function removeSelected(type: MediaType, name: string): void {
        const target = type === "Series" ? selectedSeries : selectedMovies;
        target.delete(name.toLowerCase());
        persistField(type);
        renderChips();
        renderResults(searchInput.value);
    }

    function toggleSelected(item: MediaOption): void {
        if (isSelected(item.Type, item.Name)) {
            removeSelected(item.Type, item.Name);
            return;
        }

        addSelected(item);
        persistField(item.Type);
        // Clear the query so the next search starts fresh for rapid multi-select.
        searchInput.value = "";
        renderChips();
        renderResults(searchInput.value);
        searchInput.focus();
    }

    function appendOption(item: MediaOption): void {
        const optionId = resultsId + "-option-" + String(renderedOptions.length + 1);
        const selected = isSelected(item.Type, item.Name);
        const option = el("div", { className: "media-exclusion-result", id: optionId }, optionLabel(item));
        option.setAttribute("role", "option");
        option.setAttribute("aria-selected", String(selected));
        option.classList.toggle("selected", selected);

        const optionIndex = renderedOptions.length;
        option.addEventListener("mousemove", () => setActiveOption(optionIndex));
        option.addEventListener("mousedown", (event) => event.preventDefault());
        option.addEventListener("click", () => toggleSelected(item));

        renderedOptions.push({ id: optionId, item, element: option });
        resultsEl.append(option);
    }

    function renderResults(query: string): void {
        resultsEl.replaceChildren();
        renderedOptions = [];
        resetActiveOption();

        if (isLoading) {
            openResults();
            return;
        }

        if (loadFailed) {
            closeResults();
            return;
        }

        const trimmedQuery = query.trim();
        const lower = trimmedQuery.toLowerCase();
        const matches = lower
            ? mediaOptions.filter((item) => item.Name.toLowerCase().includes(lower))
            : mediaOptions;

        if (matches.length === 0) {
            const text = hasLoaded && mediaOptions.length === 0
                ? "No Jellyfin series or movies found."
                : "No series or movies match.";
            resultsEl.append(el("div", { className: "media-exclusion-empty", role: "status" }, text));
            openResults();
            return;
        }

        for (const item of matches.slice(0, MAX_RESULTS)) {
            appendOption(item);
        }

        if (matches.length > MAX_RESULTS) {
            resultsEl.append(
                el(
                    "div",
                    { className: "media-exclusion-notice", role: "status" },
                    "Showing first 50 matches. Keep typing to narrow results.",
                ),
            );
        }

        openResults();
        setActiveOption(0);
    }

    async function ensureLoaded(): Promise<void> {
        if (hasLoaded || isLoading) return;

        isLoading = true;
        loadFailed = false;
        status.show("Loading Jellyfin media…");
        openResults();

        try {
            const loadedItems = await getExcludableMedia();
            if (!container.isConnected) return;

            mediaOptions = toMediaOptions(loadedItems);
            hasLoaded = true;
            if (mediaOptions.length === 0) {
                status.show("No Jellyfin series or movies found.");
            } else {
                status.clear();
            }
        } catch (error) {
            if (!container.isConnected) return;

            console.error("Failed to load excludable media", error);
            loadFailed = true;
            status.show("Unable to load Jellyfin media. Existing exclusions are preserved.", "var(--is-error)");
        } finally {
            isLoading = false;
            if (container.isConnected) {
                renderResults(searchInput.value);
            }
        }
    }

    function handleInput(): void {
        void ensureLoaded();
        renderResults(searchInput.value);
    }

    function handleFocusOrClick(): void {
        void ensureLoaded();
        if (hasLoaded) {
            renderResults(searchInput.value);
        }
    }

    function handleKeydown(event: KeyboardEvent): void {
        if (event.key === "ArrowDown") {
            event.preventDefault();
            void ensureLoaded();
            if (!resultsEl.classList.contains("open")) {
                renderResults(searchInput.value);
            }
            setActiveOption(activeIndex < 0 ? 0 : activeIndex + 1);
        } else if (event.key === "ArrowUp") {
            if (!resultsEl.classList.contains("open")) return;
            event.preventDefault();
            setActiveOption(activeIndex < 0 ? renderedOptions.length - 1 : activeIndex - 1);
        } else if (event.key === "Home") {
            if (!resultsEl.classList.contains("open")) return;
            event.preventDefault();
            setActiveOption(0);
        } else if (event.key === "End") {
            if (!resultsEl.classList.contains("open")) return;
            event.preventDefault();
            setActiveOption(renderedOptions.length - 1);
        } else if (event.key === "Enter") {
            if (!resultsEl.classList.contains("open") || activeIndex < 0) return;
            event.preventDefault();
            toggleSelected(renderedOptions[activeIndex].item);
        } else if (event.key === "Backspace") {
            if (searchInput.value.length > 0) return;
            const removable = selectedMovies.size > 0
                ? { type: "Movie" as MediaType, names: Array.from(selectedMovies.values()) }
                : selectedSeries.size > 0
                  ? { type: "Series" as MediaType, names: Array.from(selectedSeries.values()) }
                  : null;
            if (!removable) return;
            event.preventDefault();
            removeSelected(removable.type, removable.names[removable.names.length - 1]);
        } else if (event.key === "Escape") {
            closeResults();
        }
    }

    function handleFocusout(event: FocusEvent): void {
        const nextTarget = event.relatedTarget;
        if (!(nextTarget instanceof Node) || !control.contains(nextTarget)) {
            closeResults();
        }
    }

    function handleLoaded(): void {
        rebuildSelectedFromConfig();
        renderChips();
    }

    function handleChanged(data: unknown): void {
        const event = data as ConfigChange;
        if (event.field !== "ExcludeSeries" && event.field !== "ExcludeMovies") return;

        rebuildSelectedFromConfig();
        renderChips();
        if (resultsEl.classList.contains("open")) {
            renderResults(searchInput.value);
        }
    }

    searchInput.addEventListener("input", handleInput);
    searchInput.addEventListener("focus", handleFocusOrClick);
    searchInput.addEventListener("click", handleFocusOrClick);
    searchInput.addEventListener("keydown", handleKeydown);
    control.addEventListener("focusout", handleFocusout);
    configStore.subscribe("loaded", handleLoaded);
    configStore.subscribe("changed", handleChanged);

    if (configStore.isLoaded()) {
        handleLoaded();
    }

    return container;
}
