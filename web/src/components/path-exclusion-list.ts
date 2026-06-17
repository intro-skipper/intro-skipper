import type { LibraryLocation } from "../types.ts";
import { formatConfiguredList, splitConfiguredList } from "../configured-list.ts";
import { configStore } from "../store/config-store.ts";
import { getLibraryLocations } from "../store/jellyfin-client.ts";
import { createStatusMessage } from "./async-feedback.ts";
import { el } from "./dom.ts";
import { appendFieldMeta } from "./field-meta.ts";

const FIELD = "ExcludePaths";

let groupCounter = 0;

// Lets the user exclude analysis for media under a library folder by ticking it.
// Folders are discovered from Jellyfin's libraries (/Library/VirtualFolders), so there
// is no free-text entry or filesystem browsing; the selection is persisted to the
// comma-separated ExcludePaths config value and matched as a case-insensitive substring.
export function pathExclusionList(): HTMLElement {
    const container = el("div", { className: "input-container path-exclusion-list" });
    const idBase = "field-exclude-paths-" + String(++groupCounter);

    container.append(el("div", { className: "input-label" }, "Exclude paths"));
    appendFieldMeta(container, {
        idBase,
        description:
            "Skip analysis for media under selected library folders. Useful for excluding remote or cloud-mounted directories (e.g. Real-Debrid/Zurg) from fingerprinting. Folders are read from your Jellyfin libraries.",
    });

    const list = el("div", { className: "path-exclusion-options", id: idBase });
    const status = createStatusMessage({ display: "block" });
    container.append(status.element, list);

    let locations: LibraryLocation[] = [];
    let locationsLoaded = false;

    function togglePath(path: string, checked: boolean): void {
        const current = splitConfiguredList(configStore.get(FIELD));
        const exists = current.some((p) => p.toLowerCase() === path.toLowerCase());
        let next = current;
        if (checked && !exists) {
            next = [...current, path];
        } else if (!checked && exists) {
            next = current.filter((p) => p.toLowerCase() !== path.toLowerCase());
        }
        configStore.set(FIELD, formatConfiguredList(next));
    }

    function createRow(path: string, checked: boolean, meta: string): HTMLElement {
        const label = el("label", { className: "path-exclusion-option" });
        const input = el("input", { type: "checkbox" }) as HTMLInputElement;
        input.checked = checked;
        input.addEventListener("change", () => {
            togglePath(path, input.checked);
        });

        const text = el("span", { className: "path-exclusion-option-text" });
        text.append(el("span", { className: "path-exclusion-path" }, path));
        text.append(el("span", { className: "path-exclusion-meta" }, meta));

        label.append(input, text);
        return label;
    }

    function renderRows(): void {
        if (!configStore.isLoaded() || !locationsLoaded) {
            return;
        }

        const excluded = splitConfiguredList(configStore.get(FIELD));
        const excludedKeys = new Set(excluded.map((p) => p.toLowerCase()));
        const locationKeys = new Set(locations.map((l) => l.path.toLowerCase()));

        const rows: HTMLElement[] = [];
        for (const location of locations) {
            rows.push(
                createRow(location.path, excludedKeys.has(location.path.toLowerCase()), location.libraryName),
            );
        }

        // Preserve configured paths that are not current library folders (e.g. legacy entries)
        // so they stay visible and removable rather than being silently dropped.
        for (const path of excluded) {
            if (!locationKeys.has(path.toLowerCase())) {
                rows.push(createRow(path, true, "Not a current library folder"));
            }
        }

        list.replaceChildren(...rows);

        if (rows.length === 0) {
            status.show("No library folders found.");
        } else {
            status.clear();
        }
    }

    async function loadLocations(): Promise<void> {
        status.show("Loading library folders…");
        try {
            locations = await getLibraryLocations();
        } finally {
            locationsLoaded = true;
            renderRows();
        }
    }

    configStore.subscribe("loaded", renderRows);
    void loadLocations();
    if (configStore.isLoaded()) {
        renderRows();
    }

    return container;
}
