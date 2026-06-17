import type { FileSystemEntryInfo } from "../types.ts";
import { formatConfiguredList, splitConfiguredList } from "../configured-list.ts";
import * as api from "../store/api.ts";
import { configStore } from "../store/config-store.ts";
import { el } from "./dom.ts";
import { createStatusMessage } from "./async-feedback.ts";
import { appendFieldMeta } from "./field-meta.ts";

const FIELD = "ExcludePaths";

let groupCounter = 0;

// Excludes analysis for media under specific server folders. The user browses Jellyfin's
// filesystem (EnvironmentController) to pick an arbitrary directory and adds it; selections
// are shown as a removable list. Whole-library exclusion is intentionally NOT handled here —
// Jellyfin's per-library media-segment-provider toggle already covers that. Paths persist to
// the comma-separated ExcludePaths config and are matched as a case-insensitive substring.
export function pathExclusion(): HTMLElement {
    const container = el("div", { className: "input-container path-exclusion" });
    const idBase = "field-exclude-paths-" + String(++groupCounter);

    container.append(el("div", { className: "input-label" }, "Exclude paths"));
    appendFieldMeta(container, {
        idBase,
        description:
            "Skip analysis for media under specific server folders — useful for remote or cloud-mounted directories (e.g. Real-Debrid/Zurg). Browse to a folder and add it; any media whose path contains it (case-insensitive) is skipped. To exclude a whole library, disable the Intro Skipper media segment provider on it in Jellyfin's library settings instead.",
    });

    const selectedList = el("div", { className: "path-exclusion-selected", id: idBase });
    container.append(selectedList);

    function getPaths(): string[] {
        return configStore.isLoaded() ? splitConfiguredList(configStore.get(FIELD)) : [];
    }

    function removePath(path: string): void {
        if (!configStore.isLoaded()) {
            return;
        }
        const next = getPaths().filter((p) => p.toLowerCase() !== path.toLowerCase());
        configStore.set(FIELD, formatConfiguredList(next));
        renderSelected();
    }

    function addPath(path: string): boolean {
        if (!configStore.isLoaded()) {
            return false;
        }
        const paths = getPaths();
        if (paths.some((p) => p.toLowerCase() === path.toLowerCase())) {
            return false;
        }
        configStore.set(FIELD, formatConfiguredList([...paths, path]));
        renderSelected();
        return true;
    }

    function renderSelected(): void {
        if (!configStore.isLoaded()) {
            return;
        }

        const paths = getPaths();
        if (paths.length === 0) {
            selectedList.replaceChildren(
                el("div", { className: "path-exclusion-empty" }, "No excluded paths yet. Browse below and add one."),
            );
            return;
        }

        const rows = paths.map((path) => {
            const row = el("div", { className: "path-exclusion-chip" });
            row.append(el("span", { className: "path-exclusion-chip-path" }, path));

            const remove = el(
                "button",
                { className: "path-exclusion-chip-remove", type: "button" },
                "\u00d7",
            ) as HTMLButtonElement;
            remove.setAttribute("aria-label", "Remove excluded path " + path);
            remove.addEventListener("click", () => {
                removePath(path);
            });

            row.append(remove);
            return row;
        });

        selectedList.replaceChildren(...rows);
    }

    // --- Jellyfin filesystem browser ---
    const browser = el("div", { className: "path-browser" });
    const browserTitle = el("h3", { className: "checkbox-list-label" }, "Browse server folders");
    const currentPath = el("div", { className: "path-browser-current" }, "No folder loaded.");
    const actions = el("div", { className: "path-browser-actions" });
    const openButton = el(
        "button",
        { className: "reset-button", type: "button" },
        "Open browser",
    ) as HTMLButtonElement;
    const upButton = el("button", { className: "reset-button", type: "button" }, "Up") as HTMLButtonElement;
    const addButton = el(
        "button",
        { className: "reset-button", type: "button" },
        "Add current folder",
    ) as HTMLButtonElement;
    const list = el("div", { className: "path-browser-list" });
    const status = createStatusMessage({ display: "block" });

    let activePath: string | null = null;

    upButton.disabled = true;
    addButton.disabled = true;
    actions.append(openButton, upButton, addButton);
    browser.append(browserTitle, currentPath, actions, status.element, list);
    container.append(browser);

    function setBusy(isBusy: boolean): void {
        openButton.disabled = isBusy;
        upButton.disabled = isBusy || !activePath;
        addButton.disabled = isBusy || !activePath;
    }

    function renderEntries(entries: FileSystemEntryInfo[]): void {
        list.replaceChildren();
        if (entries.length === 0) {
            list.append(el("div", { className: "path-browser-empty" }, "No subfolders here."));
            return;
        }

        for (const entry of entries) {
            const button = el(
                "button",
                { className: "path-browser-entry", type: "button" },
                entry.Name || entry.Path,
            ) as HTMLButtonElement;
            button.title = entry.Path;
            button.addEventListener("click", () => {
                loadPath(entry.Path).catch(console.error);
            });
            list.append(button);
        }
    }

    async function loadDrives(): Promise<void> {
        setBusy(true);
        status.show("Loading server drives…");
        try {
            const result = await api.getDrives();
            if (!result.ok) {
                throw new Error(result.error ?? "Unable to load drives");
            }
            activePath = null;
            currentPath.textContent = "Server roots";
            renderEntries(result.data ?? []);
            status.show("Select a drive or root folder.");
        } catch (err: unknown) {
            list.replaceChildren();
            status.show(err instanceof Error ? err.message : "Unable to load drives.", "var(--is-error)");
        } finally {
            setBusy(false);
        }
    }

    async function loadPath(path: string): Promise<void> {
        setBusy(true);
        status.show("Loading folders…");
        try {
            const result = await api.getDirectoryContents(path);
            if (!result.ok) {
                throw new Error(result.error ?? "Unable to load folder");
            }
            activePath = path;
            currentPath.textContent = path;
            renderEntries(result.data ?? []);
            status.show("Open a subfolder, or add the current folder.");
        } catch (err: unknown) {
            status.show(err instanceof Error ? err.message : "Unable to load folder.", "var(--is-error)");
        } finally {
            setBusy(false);
        }
    }

    async function openDefaultPath(): Promise<void> {
        setBusy(true);
        status.show("Fetching Jellyfin browser path…");
        try {
            const result = await api.getDefaultDirectoryBrowser();
            if (!result.ok) {
                throw new Error(result.error ?? "Unable to load default path");
            }
            const path = result.data?.Path;
            if (path) {
                await loadPath(path);
                return;
            }
            await loadDrives();
        } catch (err: unknown) {
            status.show(
                err instanceof Error ? err.message : "Unable to fetch Jellyfin browser path.",
                "var(--is-error)",
            );
        } finally {
            setBusy(false);
        }
    }

    openButton.addEventListener("click", () => {
        openDefaultPath().catch(console.error);
    });

    upButton.addEventListener("click", async () => {
        if (!activePath) {
            return;
        }
        setBusy(true);
        status.show("Loading parent folder…");
        try {
            const result = await api.getParentPath(activePath);
            if (!result.ok) {
                throw new Error(result.error ?? "Unable to load parent folder");
            }
            if (result.data) {
                await loadPath(result.data);
            } else {
                await loadDrives();
            }
        } catch (err: unknown) {
            status.show(
                err instanceof Error ? err.message : "Unable to load parent folder.",
                "var(--is-error)",
            );
        } finally {
            setBusy(false);
        }
    });

    addButton.addEventListener("click", () => {
        if (!activePath) {
            return;
        }
        if (!configStore.isLoaded()) {
            status.show("Configuration is still loading. Try again in a moment.", "var(--is-error)");
            return;
        }
        const added = addPath(activePath);
        status.show(
            added
                ? "Added folder to Exclude paths. Save the configuration to apply it."
                : "This folder is already excluded.",
        );
    });

    configStore.subscribe("loaded", renderSelected);
    renderSelected();

    return container;
}
