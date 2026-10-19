import type { SegmentType, Tab } from "../types.ts";
import * as api from "../store/api.ts";
import { configStore } from "../store/config-store.ts";
import { el } from "../components/dom.ts";
import { confirmDialog, eraseCheckboxes } from "../components/confirm-dialog.ts";
import { MODE_OPTIONS } from "../components/segment-editor.ts";
import { tabWarning } from "../components/tab-warning.ts";

// The Global Timestamp Type value that erases every mode. The other values are
// the modes' SegmentType names.
const ALL_MODES = "all";

/**
 * Resolves a Global Timestamp Type value to the modes it erases and the words
 * the erase button and messages use for them.
 */
function eraseTarget(value: string): { modes: SegmentType[]; button: string; timestamps: string } {
    const mode = MODE_OPTIONS.find((option) => option.value === value);
    return mode
        ? {
              modes: [mode.value],
              button: `Erase All ${mode.label} Timestamps`,
              timestamps: `${mode.label.toLowerCase()} timestamps`,
          }
        : {
              modes: MODE_OPTIONS.map((option) => option.value),
              button: "Erase All Timestamps",
              timestamps: "timestamps of every type",
          };
}

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
        for (const mode of MODE_OPTIONS) {
            globalSelect.append(el("option", { value: mode.value }, mode.label));
        }
        globalSelect.append(el("option", { value: ALL_MODES }, "All Types"));
        globalSelectGroup.append(globalSelectLabel, globalSelect);

        const globalEraseBtn = el(
            "button",
            { className: "action-button danger", type: "button" },
            eraseTarget(globalSelect.value).button,
        );

        globalSelect.addEventListener("change", () => {
            globalEraseBtn.textContent = eraseTarget(globalSelect.value).button;
        });

        globalEraseBtn.addEventListener("click", async () => {
            const target = eraseTarget(globalSelect.value);
            const result = await confirmDialog({
                title: "Confirm Timestamp Erasure",
                body: "Are you sure you want to erase all previously discovered " + target.timestamps + "? User segments are kept.",
                confirmLabel: "Erase",
                checkboxes: eraseCheckboxes,
            });
            if (!result) return;

            for (const mode of target.modes) {
                const response = await api.eraseTimestamps(mode, result);
                if (!response.ok) {
                    window.Dashboard.alert("Failed to erase " + target.timestamps);
                    return;
                }
            }
            window.Dashboard.alert("Erased " + target.timestamps);
        });

        const rebuildBtn = el(
            "button",
            { className: "action-button danger", type: "button" },
            "Rebuild Local Database",
        );
        rebuildBtn.addEventListener("click", async () => {
            const result = await confirmDialog({
                title: "Confirm Database Rebuild",
                body: "Are you sure you want to rebuild the database? This requires a full Jellyfin restart to complete.",
                confirmLabel: "Rebuild",
            });
            if (!result) return;
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
        });

        const resetBtn = el(
            "button",
            { className: "action-button danger", type: "button" },
            "Reset Settings to Defaults",
        );
        resetBtn.addEventListener("click", async () => {
            const result = await confirmDialog({
                title: "Confirm Settings Reset",
                body: "Reset every setting on every tab to its default, discarding any unsaved changes? Exclusion lists, library selection, season and show overrides, and the injected skip button CSS are kept.",
                confirmLabel: "Reset",
            });
            if (!result) return;
            const response = await api.resetConfiguration();
            if (!response.ok) {
                window.Dashboard.alert("Failed to reset settings");
                return;
            }
            // The store reports its own load failure, so the reset is
            // confirmed only once the fields show the defaults.
            await configStore.load();
            window.Dashboard.alert("Settings reset to defaults");
        });

        container.append(
            globalSelectGroup,
            globalEraseBtn,
            rebuildBtn,
            tabWarning(
                "Rebuilding the database requires a full Jellyfin restart to complete, not just a dashboard restart.",
            ),
            resetBtn,
        );
    },
};
