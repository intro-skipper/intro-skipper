import type { FileSystemEntryInfo } from "../types.ts";
import { formatConfiguredList, splitConfiguredList } from "../configured-list.ts";
import * as api from "../store/api.ts";
import { configStore } from "../store/config-store.ts";
import { el } from "./dom.ts";
import { createStatusMessage } from "./async-feedback.ts";

const EXCLUDE_PATHS_FIELD = "ExcludePaths";

function appendConfiguredPath(path: string): boolean {
    const paths = splitConfiguredList(configStore.get(EXCLUDE_PATHS_FIELD));
    if (paths.some((item) => item.toLowerCase() === path.toLowerCase())) {
        return false;
    }
    paths.push(path);
    configStore.set(EXCLUDE_PATHS_FIELD, formatConfiguredList(paths));
    return true;
}


export function pathBrowser(): HTMLElement {
    const container = el("div", { className: "path-browser" });
    const title = el("h3", { className: "checkbox-list-label" }, "Browse server paths");
    const description = el(
        "div",
        { className: "field-description" },
        "Fetch directories from Jellyfin and add a selected server path to Exclude paths.",
    );
    const currentPath = el("div", { className: "path-browser-current" }, "No path loaded.");
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
        "Add current path",
    ) as HTMLButtonElement;
    const list = el("div", { className: "path-browser-list" });
    const status = createStatusMessage({ display: "block" });

    let activePath: string | null = null;

    upButton.disabled = true;
    addButton.disabled = true;
    actions.append(openButton, upButton, addButton);
    container.append(title, description, currentPath, actions, status.element, list);

    function setBusy(isBusy: boolean): void {
        openButton.disabled = isBusy;
        upButton.disabled = isBusy || !activePath;
        addButton.disabled = isBusy || !activePath;
    }

    function renderEntries(entries: FileSystemEntryInfo[]): void {
        list.replaceChildren();
        if (entries.length === 0) {
            list.append(el("div", { className: "path-browser-empty" }, "No directories found."));
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
            status.show("Select a drive or root path.");
        } catch (err: unknown) {
            list.replaceChildren();
            status.show(err instanceof Error ? err.message : "Unable to load drives.", "var(--is-error)");
        } finally {
            setBusy(false);
        }
    }

    async function loadPath(path: string): Promise<void> {
        setBusy(true);
        status.show("Loading directories…");
        try {
            const result = await api.getDirectoryContents(path);
            if (!result.ok) {
                throw new Error(result.error ?? "Unable to load directory");
            }
            activePath = path;
            currentPath.textContent = path;
            renderEntries(result.data ?? []);
            status.show("Select a child directory or add the current path.");
        } catch (err: unknown) {
            status.show(
                err instanceof Error ? err.message : "Unable to load directory.",
                "var(--is-error)",
            );
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
        status.show("Loading parent path…");
        try {
            const result = await api.getParentPath(activePath);
            if (!result.ok) {
                throw new Error(result.error ?? "Unable to load parent path");
            }
            if (result.data) {
                await loadPath(result.data);
            } else {
                await loadDrives();
            }
        } catch (err: unknown) {
            status.show(
                err instanceof Error ? err.message : "Unable to load parent path.",
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
        const added = appendConfiguredPath(activePath);
        status.show(
            added
                ? "Added path to Exclude paths. Save the configuration to apply it."
                : "This path is already in Exclude paths.",
        );
    });

    return container;
}
