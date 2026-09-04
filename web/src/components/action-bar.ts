import { el } from "./dom.ts";
import { bindStatusMessage, withDashboardLoading } from "./async-feedback.ts";
import { confirmDialog } from "./confirm-dialog.ts";
import * as api from "../store/api.ts";
import type { AnalyzerActions } from "../types.ts";
import { delay } from "../utils.ts";

// Analyzer override choices per mode, in display order. Every mode accepts
// Default, Chapter and None; the middle entries are the extra analyzers.
const ANALYZER_MODES: ReadonlyArray<{
    key: keyof AnalyzerActions;
    label: string;
    actions: readonly string[];
}> = [
    { key: "Recap", label: "Recap", actions: ["Default", "Chapter", "Chromaprint", "None"] },
    { key: "Introduction", label: "Intro", actions: ["Default", "Chapter", "Chromaprint", "None"] },
    {
        key: "Credits",
        label: "Credits",
        actions: ["Default", "Chapter", "Chromaprint", "BlackFrame", "None"],
    },
    { key: "Preview", label: "Preview", actions: ["Default", "Chapter", "None"] },
    { key: "Commercial", label: "Commercial", actions: ["Default", "Chapter", "None"] },
];

const SEGMENT_EDITOR_PLUGIN_ID = "ace21d44a4e54a85ae75acd2e24a9574";

// Whether the Segment Editor plugin is installed and active. Looked up once per
// page lifetime; a failed lookup is not cached so the next action bar retries.
let segmentEditorActive: Promise<boolean> | null = null;

function isSegmentEditorActive(): Promise<boolean> {
    if (segmentEditorActive) return segmentEditorActive;
    const lookup = api.checkPlugins().then(
        (plugins) => plugins.some((p) => p.Id === SEGMENT_EDITOR_PLUGIN_ID && p.Status === "Active"),
        (err: unknown) => {
            segmentEditorActive = null;
            throw err;
        },
    );
    segmentEditorActive = lookup;
    return lookup;
}

type ActionBarOptions = {
    onScanComplete: () => void | Promise<void>;
};

export function actionBar(opts: ActionBarOptions): {
    container: HTMLElement;
    toggle: (open: boolean) => void;
    loadForSeason: (showId: string, seasonId: string, isMovie: boolean) => Promise<void>;
    destroy: () => void;
} {
    const container = el("div", { className: "ts-action-bar" });
    container.id = "ts-action-panel";

    const actionSelects = new Map<keyof AnalyzerActions, HTMLSelectElement>();
    const analyzerGroup = el("div", { className: "ts-analyzer-group" });

    for (const mode of ANALYZER_MODES) {
        const item = el("div", { className: "ts-analyzer-item" });
        const selectId = "ts-analyzer-" + mode.key.toLowerCase();
        const labelEl = el(
            "label",
            { className: "ts-analyzer-label", for: selectId },
            mode.label,
        );
        const select = el("select", {
            id: selectId,
            name: "analyzer-" + mode.key.toLowerCase(),
        });
        for (const action of mode.actions) {
            select.append(el("option", { value: action }, action));
        }
        actionSelects.set(mode.key, select);
        item.append(labelEl);
        item.append(select);
        analyzerGroup.append(item);
    }

    const applyBtn = el(
        "button",
        { className: "ts-action-btn apply", type: "button" },
        "Save Analyzer Overrides",
    );
    const scanBtn = el(
        "button",
        { className: "ts-action-btn scan", type: "button" },
        "Scan Season",
    );
    const eraseBtn = el(
        "button",
        { className: "ts-action-btn erase", type: "button" },
        "Erase Season Timestamps",
    );

    const buttonsDiv = el("div", { className: "ts-action-buttons" });
    buttonsDiv.append(applyBtn, scanBtn, eraseBtn);

    const row = el("div", { className: "ts-action-row" });
    row.append(analyzerGroup, buttonsDiv);

    const metaRow = el("div", { className: "ts-action-meta" });
    const statusEl = el("div", { className: "ts-action-status" });
    const statusMessage = bindStatusMessage(statusEl, { display: "block" });
    const editorLink = el(
        "a",
        {
            href: "#/dashboard/plugins/" + SEGMENT_EDITOR_PLUGIN_ID + "?name=Segment Editor",
        },
        "Segment Editor \u2192",
    );
    metaRow.append(editorLink);

    container.append(row, metaRow, statusEl);

    let currentShowId = "";
    let currentSeasonId = "";
    let currentIsMovie = false;
    let destroyed = false;
    let loadVersion = 0;
    let scanVersion = 0;

    function updateActionLabels(): void {
        scanBtn.textContent = currentIsMovie ? "Scan Movie" : "Scan Season";
        eraseBtn.textContent = currentIsMovie
            ? "Erase Movie Timestamps"
            : "Erase Season Timestamps";
    }

    function resetScanButton(): void {
        scanBtn.disabled = false;
        updateActionLabels();
    }

    const handleApplyClick = async () => {
        if (destroyed) return;

        statusMessage.show("Saving analyzer overrides\u2026", "var(--is-text-muted)");

        try {
            await withDashboardLoading(async () => {
                const actions: AnalyzerActions = {};
                for (const [key, select] of actionSelects) {
                    actions[key] = select.value;
                }
                await api.updateAnalyzerActions(currentSeasonId, actions);
            });
            statusMessage.show("Analyzer overrides updated.", "var(--is-success)");
        } catch {
            statusMessage.show("Failed to update analyzer overrides.", "var(--is-error)");
        }
    };

    const pollForScanCompletion = async (scanToken: number): Promise<void> => {
        const MAX_POLL_ATTEMPTS = 300; // ~10 minutes with base interval
        const BASE_INTERVAL = 1000;
        const MAX_INTERVAL = 10_000;

        let attempts = 0;
        let interval = BASE_INTERVAL;

        while (!destroyed && scanToken === scanVersion && attempts < MAX_POLL_ATTEMPTS) {
            await delay(interval);
            if (destroyed || scanToken !== scanVersion) {
                return;
            }

            attempts++;
            const status = await api.getScanStatus();

            if (destroyed || scanToken !== scanVersion) {
                return;
            }

            if (status.ok && !status.data?.isRunning) {
                resetScanButton();
                // The onScanComplete callback owns the refresh (and may withhold
                // it over unsaved edits), so this message must not claim one.
                statusMessage.show("Scan finished.", "var(--is-success)");
                await Promise.resolve(opts.onScanComplete());
                return;
            }

            if (!status.ok) {
                interval = Math.min(interval * 2, MAX_INTERVAL);
            } else {
                interval = BASE_INTERVAL;
            }
        }

        if (destroyed || scanToken !== scanVersion) {
            return;
        }

        resetScanButton();
        statusMessage.show(
            "Scan status polling timed out. Refresh to check results.",
            "var(--is-warning)",
        );
    };

    const handleScanClick = async () => {
        if (destroyed) return;

        const scanToken = ++scanVersion;
        scanBtn.disabled = true;
        statusMessage.show("Starting scan\u2026", "var(--is-text-muted)");
        try {
            const response = await withDashboardLoading(async () => {
                const seasonId = currentIsMovie ? currentShowId : currentSeasonId;
                return api.scanSeason(currentShowId, seasonId);
            });

            if (destroyed || scanToken !== scanVersion) {
                return;
            }

            if (response.status === 409) {
                statusMessage.show("A scan is already in progress.", "var(--is-warning)");
            } else if (!response.ok) {
                resetScanButton();
                statusMessage.show("Unable to start the scan.", "var(--is-error)");
                return;
            }

            scanBtn.textContent = "Scan in progress\u2026";
            statusMessage.show(
                "Scan in progress\u2026 This can take several minutes.",
                "var(--is-text-muted)",
            );

            void pollForScanCompletion(scanToken).catch(console.error);
        } catch {
            resetScanButton();
            statusMessage.show("Unable to start the scan.", "var(--is-error)");
        }
    };

    const handleEraseClick = async () => {
        if (destroyed) return;

        const label = currentIsMovie ? "movie" : "season";
        // A movie's season-state key is its own ID, so it fills both route segments.
        const seasonId = currentIsMovie ? currentShowId : currentSeasonId;
        const url =
            "Intros/Show/" +
            encodeURIComponent(currentShowId) +
            "/" +
            encodeURIComponent(seasonId);
        const result = await confirmDialog({
            title: "Confirm Timestamp Erasure",
            body: "Are you sure you want to erase all timestamps for this " + label + "?",
            confirmLabel: "Erase",
            checkbox: { label: "Include cached fingerprints" },
        });
        if (destroyed) return;
        if (!result) return;
        statusMessage.show("Erasing timestamps\u2026", "var(--is-text-muted)");
        try {
            const response = await api.eraseItemTimestamps(url, result.checkboxChecked);
            if (!response.ok) {
                statusMessage.show("Failed to erase timestamps.", "var(--is-error)");
                return;
            }
            statusMessage.show("Timestamps erased.", "var(--is-success)");
            await Promise.resolve(opts.onScanComplete());
        } catch {
            statusMessage.show("Failed to erase timestamps.", "var(--is-error)");
        }
    };

    // Resolve the editor link at construction time so it's ready before the
    // user navigates to a specific season. A failed lookup leaves the generic
    // plugin page link in place.
    isSegmentEditorActive()
        .then((isActive) => {
            if (isActive && !destroyed) {
                editorLink.setAttribute("href", "#/configurationpage?name=Segment%20Editor");
            }
        })
        .catch(() => {});

    applyBtn.addEventListener("click", handleApplyClick);
    scanBtn.addEventListener("click", handleScanClick);
    eraseBtn.addEventListener("click", handleEraseClick);

    return {
        container,

        toggle(open: boolean) {
            container.classList.toggle("open", open);
        },

        async loadForSeason(showId: string, seasonId: string, isMovie: boolean) {
            if (destroyed) return;

            const loadToken = ++loadVersion;
            scanVersion += 1;
            currentShowId = showId;
            currentSeasonId = seasonId;
            currentIsMovie = isMovie;

            resetScanButton();
            statusMessage.clear();

            // Analyzer overrides only apply to seasons, not single movies.
            analyzerGroup.style.display = isMovie ? "none" : "";
            applyBtn.style.display = isMovie ? "none" : "";

            if (!isMovie) {
                const result = await api.getAnalyzerActions(seasonId);
                if (destroyed || loadToken !== loadVersion) {
                    return;
                }

                const actions: AnalyzerActions = result.ok && result.data ? result.data : {};
                for (const [key, select] of actionSelects) {
                    select.value = actions[key] ?? "Default";
                }
            }

            // Disable the button if another scan is already running server-side.
            const status = await api.getScanStatus();
            if (destroyed || loadToken !== loadVersion) {
                return;
            }

            if (status.ok && status.data?.isRunning) {
                scanBtn.disabled = true;
                scanBtn.textContent = "Scan in progress\u2026";
                statusMessage.show(
                    "Scan in progress\u2026 This can take several minutes.",
                    "var(--is-text-muted)",
                );
            }
        },

        destroy() {
            destroyed = true;
            loadVersion += 1;
            scanVersion += 1;
            applyBtn.removeEventListener("click", handleApplyClick);
            scanBtn.removeEventListener("click", handleScanClick);
            eraseBtn.removeEventListener("click", handleEraseClick);
        },
    };
}
