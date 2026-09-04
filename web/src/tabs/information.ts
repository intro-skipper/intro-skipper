import type { SupportBundleEntry, SupportBundleSection, Tab } from "../types.ts";
import * as api from "../store/api.ts";
import { el } from "../components/dom.ts";
import { bindStatusMessage } from "../components/async-feedback.ts";

// Copies text to the clipboard. Dashboards served over plain HTTP have no
// navigator.clipboard, so fall back to selecting an offscreen textarea.
async function copyText(text: string): Promise<boolean> {
    try {
        if (navigator.clipboard) {
            await navigator.clipboard.writeText(text);
            return true;
        }
    } catch {
        /* fall through to the legacy path */
    }

    const scratch = el("textarea", { readonly: "", "aria-hidden": "true" });
    scratch.value = text;
    scratch.style.position = "fixed";
    scratch.style.opacity = "0";
    document.body.append(scratch);
    scratch.select();
    let copied = false;
    try {
        copied = document.execCommand("copy");
    } catch {
        copied = false;
    }
    scratch.remove();
    return copied;
}

function renderEntries(entries: SupportBundleEntry[]): HTMLElement {
    const grid = el("dl", { className: "support-grid" });
    for (const entry of entries) {
        grid.append(el("dt", {}, entry.Label), el("dd", {}, entry.Value));
    }
    return grid;
}

function renderBody(section: SupportBundleSection): HTMLElement {
    if (typeof section.Text === "string") {
        return el("pre", { className: "support-pre" }, section.Text);
    }
    const entries = section.Entries ?? [];
    return entries.length > 0
        ? renderEntries(entries)
        : el("div", { className: "support-none" }, "None");
}

function sizeHint(section: SupportBundleSection): string {
    if (typeof section.Text === "string") {
        const lines = section.Text.length === 0 ? 0 : section.Text.trimEnd().split("\n").length;
        return lines === 1 ? "1 line" : `${lines} lines`;
    }
    const count = section.Entries?.length ?? 0;
    return count === 1 ? "1 item" : `${count} items`;
}

// Visible sections get an uppercase label and their body; collapsed sections
// become fold rows, introduced once by a "Details" label.
function renderSections(sections: SupportBundleSection[]): HTMLElement[] {
    const nodes: HTMLElement[] = [];
    let foldsStarted = false;

    for (const section of sections) {
        if (!section.Collapsed) {
            const block = el("div", { className: "support-section" });
            block.append(el("div", { className: "support-title" }, section.Title), renderBody(section));
            nodes.push(block);
            continue;
        }

        if (!foldsStarted) {
            foldsStarted = true;
            nodes.push(el("div", { className: "support-title" }, "Details"));
        }
        const fold = el("details", { className: "support-fold" });
        const summary = el("summary", {}, section.Title);
        summary.append(el("span", { className: "support-fold-meta" }, sizeHint(section)));
        fold.append(summary, renderBody(section));
        nodes.push(fold);
    }

    return nodes;
}

export const informationTab: Tab = {
    id: "information",
    label: "Information",
    render(container) {
        const supportContainer = el("section", { className: "tab-readonly-section" });
        const supportTitle = el(
            "h3",
            { className: "checkbox-list-label", id: "support-log-label" },
            "Intro Skipper Support Log",
        );
        const copyButtonEl = el(
            "button",
            { className: "raised button-submit", type: "button" },
            "Copy to Clipboard",
        );
        copyButtonEl.disabled = true;
        const supportHead = el("div", { className: "support-head" });
        supportHead.append(supportTitle, copyButtonEl);

        const supportStatusEl = el("div", { className: "status-message", id: "support-log-status" });
        const supportStatus = bindStatusMessage(supportStatusEl, { display: "block" });
        copyButtonEl.setAttribute("aria-describedby", supportStatusEl.id);

        const supportSections = el("div", {});
        supportSections.setAttribute("aria-labelledby", supportTitle.id);
        supportContainer.append(supportHead, supportStatusEl, supportSections);

        let markdown = "";
        let manualCopyArea: HTMLTextAreaElement | undefined;
        copyButtonEl.addEventListener("click", async () => {
            if (!markdown) return;
            if (await copyText(markdown)) {
                manualCopyArea?.remove();
                manualCopyArea = undefined;
                window.Dashboard.alert("Support bundle copied to clipboard");
                return;
            }
            // No clipboard API and execCommand failed: show the Markdown selected in a
            // textarea so the user can still copy it manually.
            if (!manualCopyArea) {
                manualCopyArea = el("textarea", { readonly: "", rows: "12" });
                manualCopyArea.setAttribute("aria-labelledby", supportTitle.id);
                supportHead.after(manualCopyArea);
            }
            manualCopyArea.value = markdown;
            manualCopyArea.focus();
            manualCopyArea.setSelectionRange(0, markdown.length);
            window.Dashboard.alert("Press Ctrl+C to copy support bundle");
        });

        async function loadSupportBundle(): Promise<void> {
            supportStatus.show("Loading support log…");
            try {
                const bundle = await api.getSupportBundle();
                markdown = bundle.Markdown;
                supportSections.replaceChildren(...renderSections(bundle.Sections));
                copyButtonEl.disabled = !markdown;
                if (bundle.Sections.length === 0) {
                    supportStatus.show("Support log is empty.");
                } else {
                    supportStatus.clear();
                }
            } catch {
                markdown = "";
                supportSections.replaceChildren();
                copyButtonEl.disabled = true;
                supportStatus.show("Failed to load support log.", "var(--is-error)");
            }
        }

        loadSupportBundle().catch(console.error);

        // Storage usage — structured list with progress bars.
        const storageContainer = el("section", { className: "tab-readonly-section" });
        const storageTitle = el("h3", { className: "checkbox-list-label" }, "Storage Usage");
        storageTitle.id = "storage-usage-label";
        const storageDesc = el(
            "div",
            { className: "field-description" },
            "See how much space each library uses.",
        );
        const storageStatusEl = el("div", {
            className: "status-message",
            id: "storage-usage-status",
        });
        const storageStatus = bindStatusMessage(storageStatusEl, { display: "block" });
        storageStatus.show("Loading storage usage…");
        const storageList = el("div", {});
        storageList.setAttribute("aria-labelledby", storageTitle.id);
        storageContainer.append(storageTitle, storageDesc, storageStatusEl, storageList);

        function formatSize(bytes: number): string {
            if (bytes <= 0) return "0 B";
            const units = ["B", "KB", "MB", "GB", "TB"];
            const i = Math.floor(Math.log(bytes) / Math.log(1024));
            return (bytes / Math.pow(1024, i)).toFixed(1) + " " + units[i];
        }

        function barColor(pct: number): string {
            if (pct >= 90) return "var(--is-error)";
            if (pct >= 75) return "var(--is-warning)";
            return "var(--is-success)";
        }

        function buildListItem(
            name: string,
            path: string,
            used: number,
            free: number,
        ): HTMLElement {
            const total = used + free;
            const pct = total > 0 ? (used / total) * 100 : 0;

            const item = el("li", { className: "storage-item" });

            const text = el("div", { className: "storage-item-body" });
            text.append(el("div", { className: "storage-item-name" }, name));
            text.append(el("div", { className: "storage-item-path" }, path));

            const track = el("div", { className: "storage-bar-track" });
            const fill = el("div", {
                className: "storage-bar-fill",
                style: `width:${pct.toFixed(1)}%;background:${barColor(pct)};`,
            });
            track.append(fill);
            text.append(track);

            text.append(
                el(
                    "div",
                    { className: "storage-item-usage" },
                    `${formatSize(used)} / ${formatSize(total)}`,
                ),
            );

            item.append(text);
            return item;
        }

        async function loadStorageUsage(): Promise<void> {
            storageStatus.show("Loading storage usage…");
            try {
                const libraries = await api.getStorageUsage();
                storageList.replaceChildren();
                if (libraries.length === 0) {
                    storageStatus.show("Storage usage is empty.");
                    return;
                }
                const list = el("ul", { className: "storage-list" });
                for (const lib of libraries) {
                    for (const folder of lib.Folders) {
                        list.append(
                            buildListItem(
                                lib.Name,
                                folder.Path,
                                folder.UsedSpace,
                                folder.FreeSpace,
                            ),
                        );
                    }
                }
                storageList.append(list);
                storageStatus.show("Storage usage loaded.");
            } catch {
                storageList.replaceChildren();
                storageStatus.show("Failed to load storage usage.", "var(--is-error)");
            }
        }

        loadStorageUsage().catch(console.error);

        container.append(supportContainer, storageContainer);
    },
};
