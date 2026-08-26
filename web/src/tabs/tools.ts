import type { Tab } from "../types.ts";
import * as api from "../store/api.ts";
import { el } from "../components/dom.ts";
import { confirmDialog } from "../components/confirm-dialog.ts";
import { appendTabContent } from "../components/tab-layout.ts";
import { tabWarning } from "../components/tab-warning.ts";

export const toolsTab: Tab = {
    id: "tools",
    label: "Tools",

    render(container) {
        const globalSelectId = "global-timestamp-type";
        const globalSelectGroup = el("div", { className: "select-container" });
        const globalSelectLabel = el(
            "label",
            { className: "select-label", for: globalSelectId },
            "Global Timestamp Type",
        );
        const globalSelect = el("select", {
            id: globalSelectId,
            name: "global-timestamp-type",
        });
        globalSelect.append(el("option", { value: "introduction" }, "Introduction"));
        globalSelect.append(el("option", { value: "recap" }, "Recap"));
        globalSelect.append(el("option", { value: "credits" }, "Credits"));
        globalSelect.append(el("option", { value: "preview" }, "Preview"));
        globalSelect.append(el("option", { value: "commercial" }, "Commercial"));
        globalSelectGroup.append(globalSelectLabel, globalSelect);

        const globalEraseBtn = el(
            "button",
            { className: "action-button raised block", type: "button" },
            "Erase All Introduction Timestamps",
        );

        globalSelect.addEventListener("change", () => {
            globalEraseBtn.textContent =
                "Erase All " +
                globalSelect.value.charAt(0).toUpperCase() +
                globalSelect.value.slice(1) +
                " Timestamps";
        });

        globalEraseBtn.addEventListener("click", async () => {
            const typeMap: Record<string, string> = {
                introduction: "Introduction",
                recap: "Recap",
                credits: "Credits",
                preview: "Preview",
                commercial: "Commercial",
            };
            const type = typeMap[globalSelect.value];
            if (!type) return;

            const result = await confirmDialog({
                title: "Confirm Timestamp Erasure",
                body:
                    "Are you sure you want to erase all previously discovered " +
                    globalSelect.value +
                    " timestamps?",
                confirmLabel: "Erase",
                checkbox: { label: "Include cached fingerprint files" },
            });
            if (!result) return;
            try {
                const response = await api.eraseTimestamps(type, result.checkboxChecked);
                if (!response.ok) {
                    window.Dashboard.alert("Failed to erase " + type + " timestamps");
                    return;
                }
                window.Dashboard.alert(type + " timestamps erased");
            } catch {
                window.Dashboard.alert("Failed to erase " + type + " timestamps");
            }
        });

        const rebuildBtn = el(
            "button",
            { className: "action-button raised block", type: "button" },
            "Rebuild Local Database",
        );
        rebuildBtn.addEventListener("click", async () => {
            const result = await confirmDialog({
                title: "Confirm Database Rebuild",
                body: "Are you sure you want to rebuild the database? This requires a full Jellyfin restart to complete.",
                confirmLabel: "Rebuild",
            });
            if (!result) return;
            try {
                let response = await api.rebuildDatabase();
                if (response.status === 409) {
                    // The server refused because the existing database cannot be read
                    // for backup; rebuilding means starting empty.
                    const discard = await confirmDialog({
                        title: "Database Unreadable",
                        body: "The existing database could not be read for backup. Rebuilding will discard all stored timestamps and start empty. Continue?",
                        confirmLabel: "Discard and Rebuild",
                    });
                    if (!discard) return;
                    response = await api.rebuildDatabase({ forceCleanOnBackupFailure: true });
                }
                if (!response.ok) {
                    window.Dashboard.alert("Failed to rebuild database");
                    return;
                }
                window.Dashboard.alert(
                    "Database rebuild initiated. A full Jellyfin restart is required.",
                );
            } catch {
                window.Dashboard.alert("Failed to rebuild database");
            }
        });

        appendTabContent(
            container,
            globalSelectGroup,
            globalEraseBtn,
            rebuildBtn,
            tabWarning(
                "Rebuilding the database requires a full Jellyfin restart to complete, not just a dashboard restart.",
            ),
        );
    },
};
