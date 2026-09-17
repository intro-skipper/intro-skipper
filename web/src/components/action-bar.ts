import { el } from "./dom.ts";
import { bindStatusMessage, withDashboardLoading } from "./async-feedback.ts";
import { confirmDialog } from "./confirm-dialog.ts";
import * as api from "../store/api.ts";
import type { AnalysisOverrides, AnalyzerActions, SeasonItem } from "../types.ts";
import { configSchema, type ConfigKey } from "../config/schema.ts";
import { abortable, childScope, delay, ignoreAbort, isAbortError } from "../lifecycle.ts";
import { configStore } from "../store/config-store.ts";

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
    const lookup = api.checkPlugins().then((result) => {
        if (!result.ok) {
            segmentEditorActive = null;
            return false;
        }
        return result.data.some((p) => p.Id === SEGMENT_EDITOR_PLUGIN_ID && p.Status === "Active");
    });
    segmentEditorActive = lookup;
    return lookup;
}

type ActionBarOptions = {
    onScanComplete: () => void | Promise<void>;
    /** The Timestamps tab's lifetime. The bar's listeners and subscriptions end with it. */
    signal: AbortSignal;
};

// How a watched scan ended. "unavailable" means the status endpoint kept
// failing, so the scan may still be running.
type ScanOutcome = "completed" | "failed" | "unavailable";

export function actionBar({ onScanComplete, signal }: ActionBarOptions): {
    container: HTMLElement;
    toggle: (open: boolean) => void;
    prepareForShow: (showId: string) => void;
    /** Loads the season's overrides and scan state. `panel` is the season panel's lifetime. */
    loadForSeason: (
        showId: string,
        seasonId: string,
        isMovie: boolean,
        panel: AbortSignal,
        seriesSeasons?: readonly SeasonItem[],
    ) => Promise<void>;
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

    const analysisWindow = el("fieldset", { className: "ts-analysis-window" });
    const analysisWindowLegend = el("legend", {}, "Analysis window");
    const analysisWindowDescription = el(
        "p",
        { className: "ts-action-description" },
        "Optional per-season limits. Leave a field blank to inherit the global Analysis settings.",
    );
    const analysisWindowGrid = el("div", { className: "ts-analysis-window-grid" });

    function overrideField(
        id: string,
        label: string,
        description: string,
        min: string,
        max?: string,
    ): HTMLInputElement {
        const field = el("div", { className: "ts-override-field" });
        const labelEl = el("label", { className: "ts-override-label", for: id }, label);
        const input = el("input", {
            type: "number",
            id,
            min,
            ...(max ? { max } : {}),
            placeholder: "Global default",
            inputmode: "numeric",
        });
        const descriptionEl = el("span", { className: "ts-override-description" }, description);
        field.append(labelEl, input, descriptionEl);
        analysisWindowGrid.append(field);
        return input;
    }

    // Same limits as the global fields, so an override can never be a value the
    // global setting would reject.
    const percentLimits = configSchema.AnalysisPercent;
    const lengthLimits = configSchema.AnalysisLengthLimit;
    const analysisPercentInput = overrideField(
        "ts-analysis-percent-override",
        "Percent of media to analyze",
        "Percentage of each item's runtime.",
        String(percentLimits.min),
        String(percentLimits.max),
    );
    const analysisLengthInput = overrideField(
        "ts-analysis-length-override",
        "Maximum runtime to analyze (minutes)",
        "Upper limit for each item.",
        String(lengthLimits.min),
    );
    const previewOverrideField = el("div", { className: "ts-preview-override" });
    const previewOverrideLabel = el(
        "label",
        { className: "ts-override-label", for: "ts-preview-from-credits-override" },
        "Set after credits scene as preview",
    );
    const previewOverrideSelect = el("select", {
        id: "ts-preview-from-credits-override",
        name: "preview-from-credits-override",
    });
    const previewDefaultOption = el("option", { value: "default" }, "Global default");
    previewOverrideSelect.append(
        previewDefaultOption,
        el("option", { value: "enabled" }, "Enabled"),
        el("option", { value: "disabled" }, "Disabled"),
    );
    function updatePreviewDefaultLabel(): void {
        previewDefaultOption.textContent = configStore.isLoaded()
            ? configStore.get("AnimePreviewFromCreditsEnd")
                ? "Global: Anime only"
                : "Global: Disabled"
            : "Global default";
    }
    configStore.subscribe("loaded", updatePreviewDefaultLabel, { signal });
    configStore.subscribe(
        "changed",
        ({ field }: { field: ConfigKey }) => {
            if (field === "AnimePreviewFromCreditsEnd") updatePreviewDefaultLabel();
        },
        { signal },
    );
    updatePreviewDefaultLabel();
    const previewOverrideDescription = el(
        "span",
        { className: "ts-override-description" },
        "Creates a preview from the end of credits to the next credits block or episode end.",
    );
    previewOverrideField.append(previewOverrideLabel, previewOverrideSelect, previewOverrideDescription);
    const resetWindowBtn = el(
        "button",
        { className: "ts-reset-overrides", type: "button" },
        "Use global defaults",
    );
    resetWindowBtn.addEventListener(
        "click",
        () => {
            analysisPercentInput.value = "";
            analysisLengthInput.value = "";
            previewOverrideSelect.value = "default";
        },
        { signal },
    );
    analysisWindow.append(
        analysisWindowLegend,
        analysisWindowDescription,
        analysisWindowGrid,
        previewOverrideField,
        resetWindowBtn,
    );

    const applyBtn = el(
        "button",
        { className: "ts-action-btn apply", type: "button" },
        "Save Overrides",
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

    const fullSeriesId = "ts-full-series";
    const fullSeriesCheckbox = el("input", {
        type: "checkbox",
        id: fullSeriesId,
    });
    const fullSeriesLabel = el(
        "label",
        { className: "ts-full-series", for: fullSeriesId },
        "Full Series",
    );
    fullSeriesLabel.prepend(fullSeriesCheckbox);

    const buttonsDiv = el("div", { className: "ts-action-buttons" });
    buttonsDiv.append(applyBtn, scanBtn, eraseBtn);

    const editorLink = el(
        "a",
        {
            className: "ts-action-editor-link",
            href: "#/dashboard/plugins/" + SEGMENT_EDITOR_PLUGIN_ID + "?name=Segment Editor",
        },
        "Segment Editor →",
    );

    const topRow = el("div", { className: "ts-action-top-row" });
    topRow.append(analyzerGroup, editorLink);

    const scopeRow = el("div", { className: "ts-action-scope" });
    const scopeHint = el("span", { className: "ts-action-scope-hint" });
    scopeRow.append(fullSeriesLabel, scopeHint);
    const bottomRow = el("div", { className: "ts-action-bottom-row" });
    bottomRow.append(scopeRow, buttonsDiv);

    const statusEl = el("div", { className: "ts-action-status" });
    const statusMessage = bindStatusMessage(statusEl, { display: "block" });

    container.append(topRow, analysisWindow, bottomRow, statusEl);

    let currentShowId = "";
    let currentSeasonId = "";
    let currentIsMovie = false;
    let currentSeriesSeasons: readonly SeasonItem[] = [];
    let analysisOverridesLoaded = false;
    // The season panel the bar currently serves. Requests for it take this signal,
    // so a load or save abandoned by navigation stops at its next await.
    let panel: AbortSignal = signal;
    // The scan being watched, if any. A new scan or a new season load ends it.
    let scanScope: AbortController | null = null;

    function startScan(): AbortSignal {
        scanScope?.abort();
        scanScope = childScope(panel);
        return scanScope.signal;
    }

    function updateApplyAvailability(): void {
        applyBtn.disabled = currentIsMovie || !analysisOverridesLoaded;
        if (!analysisOverridesLoaded && !currentIsMovie) {
            applyBtn.title = "Analysis-window settings are still loading.";
        } else {
            applyBtn.removeAttribute("title");
        }
    }

    function updateActionLabels(): void {
        scopeHint.textContent = fullSeriesCheckbox.checked
            ? "Every season in this series"
            : "Current season only";
        scanBtn.textContent = currentIsMovie
            ? "Scan Movie"
            : fullSeriesCheckbox.checked
              ? "Scan Series"
              : "Scan Season";
        eraseBtn.textContent = currentIsMovie
            ? "Erase Movie Timestamps"
            : fullSeriesCheckbox.checked
              ? "Erase Series Timestamps"
              : "Erase Season Timestamps";
    }

    function resetScanButton(): void {
        scanBtn.disabled = false;
        fullSeriesCheckbox.disabled = false;
        updateActionLabels();
    }

    function targetSeasonIds(): string[] {
        if (currentIsMovie || !fullSeriesCheckbox.checked || currentSeriesSeasons.length === 0) {
            return [currentIsMovie ? currentShowId : currentSeasonId];
        }

        return currentSeriesSeasons.map((season) => season.Id);
    }

    function seasonUrl(showId: string, seasonId: string): string {
        return (
            "Intros/Show/" +
            encodeURIComponent(showId) +
            "/" +
            encodeURIComponent(seasonId)
        );
    }

    function readOverride(input: HTMLInputElement, label: string, min: number, max?: number): number | null {
        if (input.value.trim() === "") return null;
        const value = Number(input.value);
        if (!Number.isInteger(value) || value < min || (max !== undefined && value > max)) {
            throw new Error(label + " is outside the supported range.");
        }
        return value;
    }

    fullSeriesCheckbox.addEventListener("change", updateActionLabels, { signal });

    async function applyOverrides(): Promise<void> {
        if (!analysisOverridesLoaded) return;

        const operation = panel;
        const seasonIds = targetSeasonIds();
        const actions: AnalyzerActions = {};
        for (const [key, select] of actionSelects) {
            actions[key] = select.value;
        }

        let overrides: AnalysisOverrides;
        try {
            overrides = {
                AnalysisPercent: readOverride(
                    analysisPercentInput,
                    "Percent",
                    percentLimits.min,
                    percentLimits.max,
                ),
                AnalysisLengthLimit: readOverride(
                    analysisLengthInput,
                    "Maximum runtime",
                    lengthLimits.min,
                ),
                PreviewFromCreditsEnd:
                    previewOverrideSelect.value === "default"
                        ? null
                        : previewOverrideSelect.value === "enabled",
            };
        } catch (err) {
            statusMessage.show(err instanceof Error ? err.message : "Invalid analysis overrides.", "var(--is-error)");
            return;
        }

        statusMessage.show("Saving overrides…", "var(--is-text-muted)");

        try {
            await withDashboardLoading(async () => {
                for (const seasonId of seasonIds) {
                    const response = await api.updateAnalyzerActions(seasonId, actions, operation);
                    if (!response.ok) {
                        throw new Error("Failed to update analyzer overrides");
                    }
                    const windowResponse = await api.updateAnalysisOverrides(
                        seasonId,
                        overrides,
                        operation,
                    );
                    if (!windowResponse.ok) {
                        throw new Error("Failed to update analysis window");
                    }
                }
            });
            statusMessage.show("Overrides updated.", "var(--is-success)");
        } catch (err) {
            if (isAbortError(err)) return;
            statusMessage.show("Failed to update overrides.", "var(--is-error)");
        }
    }

    // Waits for the season's queued scan. The scan may wait behind a running pass
    // for as long as that pass takes, so only repeated status failures end the
    // wait early. Rejects with an AbortError when `scan` aborts.
    async function awaitScan(seasonId: string, scan: AbortSignal): Promise<ScanOutcome> {
        const MAX_FAILURES = 30;
        const BASE_INTERVAL = 1000;
        const MAX_INTERVAL = 10_000;

        let failures = 0;
        let interval = BASE_INTERVAL;

        while (true) {
            await delay(interval, scan);
            const status = await api.getScanStatus(seasonId, scan);

            if (status.ok && status.data) {
                if (!status.data.isQueued) return status.data.failed ? "failed" : "completed";
                failures = 0;
                interval = BASE_INTERVAL;
                continue;
            }

            failures++;
            if (failures >= MAX_FAILURES) return "unavailable";
            interval = Math.min(interval * 2, MAX_INTERVAL);
        }
    }

    // Puts the buttons back and reports how the scan ended. onScanComplete owns
    // the refresh (and may withhold it over unsaved edits), so the message must
    // not claim one.
    async function finishScan(outcome: ScanOutcome): Promise<void> {
        resetScanButton();
        if (outcome === "unavailable") {
            statusMessage.show(
                "Scan status unavailable. Refresh to check results.",
                "var(--is-warning)",
            );
            return;
        }
        if (outcome === "failed") {
            statusMessage.show("Scan failed. See the server log.", "var(--is-error)");
        } else {
            statusMessage.show("Scan finished.", "var(--is-success)");
        }
        await Promise.resolve(onScanComplete());
    }

    async function runScan(): Promise<void> {
        const scan = startScan();
        scanBtn.disabled = true;
        fullSeriesCheckbox.disabled = true;
        const showId = currentShowId;
        const seasonIds = targetSeasonIds();
        const isSeriesScan = !currentIsMovie && fullSeriesCheckbox.checked;
        statusMessage.show("Starting scan…", "var(--is-text-muted)");
        try {
            for (let index = 0; index < seasonIds.length; index++) {
                const progress = isSeriesScan
                    ? " (" + String(index + 1) + "/" + String(seasonIds.length) + ")"
                    : "";
                statusMessage.show("Starting scan" + progress + "…", "var(--is-text-muted)");
                const response = await withDashboardLoading(() =>
                    api.scanSeason(showId, seasonIds[index], scan),
                );

                if (!response.ok) {
                    resetScanButton();
                    statusMessage.show("Unable to start the scan.", "var(--is-error)");
                    return;
                }

                scanBtn.textContent = "Scan in progress…";
                statusMessage.show(
                    (isSeriesScan ? "Scanning series" + progress : "Scan queued") +
                        "… This can take several minutes.",
                    "var(--is-text-muted)",
                );

                const outcome = await awaitScan(seasonIds[index], scan);
                if (outcome !== "completed") {
                    await finishScan(outcome);
                    return;
                }
            }

            await finishScan("completed");
        } catch (err) {
            if (isAbortError(err)) return;
            resetScanButton();
            statusMessage.show("Unable to start the scan.", "var(--is-error)");
        }
    }

    async function eraseTimestamps(): Promise<void> {
        const isSeriesErase = !currentIsMovie && fullSeriesCheckbox.checked;
        const label = currentIsMovie ? "movie" : isSeriesErase ? "series" : "season";
        const showId = currentShowId;
        const seasonIds = targetSeasonIds();
        const result = await abortable(
            confirmDialog({
                title: "Confirm Timestamp Erasure",
                body: "Are you sure you want to erase all timestamps for this " + label + "?",
                confirmLabel: "Erase",
                checkbox: { label: "Include cached fingerprints" },
            }),
            signal,
        );
        if (!result) return;
        eraseBtn.disabled = true;
        fullSeriesCheckbox.disabled = true;
        statusMessage.show("Erasing timestamps…", "var(--is-text-muted)");
        try {
            for (const seasonId of seasonIds) {
                const response = await api.eraseItemTimestamps(
                    seasonUrl(showId, seasonId),
                    result.checkboxChecked,
                    signal,
                );
                if (!response.ok) {
                    // Jellyfin can list seasons with no queued episodes. The
                    // season erase endpoint reports those as 404, which is a
                    // successful no-op for a full-series erase.
                    if (isSeriesErase && response.status === 404) continue;
                    statusMessage.show("Failed to erase timestamps.", "var(--is-error)");
                    return;
                }
            }
            statusMessage.show("Timestamps erased.", "var(--is-success)");
            await Promise.resolve(onScanComplete());
        } catch (err) {
            if (isAbortError(err)) return;
            statusMessage.show("Failed to erase timestamps.", "var(--is-error)");
        } finally {
            eraseBtn.disabled = false;
            fullSeriesCheckbox.disabled = false;
        }
    }

    // Resolve the editor link at construction time so it's ready before the
    // user navigates to a specific season. A failed lookup leaves the generic
    // plugin page link in place.
    void isSegmentEditorActive().then((isActive) => {
        if (isActive && !signal.aborted) {
            editorLink.setAttribute("href", "#/configurationpage?name=Segment%20Editor");
        }
    });

    applyBtn.addEventListener("click", () => void applyOverrides().catch(ignoreAbort), { signal });
    scanBtn.addEventListener("click", () => void runScan().catch(ignoreAbort), { signal });
    eraseBtn.addEventListener("click", () => void eraseTimestamps().catch(ignoreAbort), { signal });

    return {
        container,

        toggle(open: boolean) {
            container.classList.toggle("open", open);
        },

        prepareForShow(showId: string) {
            scanScope?.abort();
            scanScope = null;
            currentShowId = showId;
            currentSeasonId = "";
            currentIsMovie = false;
            currentSeriesSeasons = [];
            fullSeriesCheckbox.checked = false;
            analysisOverridesLoaded = false;
            fullSeriesLabel.style.display = "";
            // No season yet, so `panel` is stale: Scan stays off until loadForSeason.
            scanBtn.disabled = true;
            fullSeriesCheckbox.disabled = false;
            updateActionLabels();
            updateApplyAvailability();
            statusMessage.clear();
        },

        async loadForSeason(showId, seasonId, isMovie, panelSignal, seriesSeasons) {
            panel = panelSignal;
            scanScope?.abort();
            scanScope = null;
            if (currentShowId !== showId) {
                fullSeriesCheckbox.checked = false;
                currentSeriesSeasons = [];
            }
            currentShowId = showId;
            currentSeasonId = seasonId;
            currentIsMovie = isMovie;
            analysisOverridesLoaded = false;
            if (seriesSeasons) {
                currentSeriesSeasons = seriesSeasons;
            }

            resetScanButton();
            updateApplyAvailability();
            statusMessage.clear();

            // Analyzer overrides only apply to seasons, not single movies.
            analyzerGroup.style.display = isMovie ? "none" : "";
            applyBtn.style.display = isMovie ? "none" : "";
            fullSeriesLabel.style.display = isMovie ? "none" : "";
            scopeRow.style.display = isMovie ? "none" : "";
            analysisWindow.style.display = isMovie ? "none" : "";

            if (!isMovie) {
                const [result, overrideResult] = await Promise.all([
                    api.getAnalyzerActions(seasonId, panel),
                    api.getAnalysisOverrides(seasonId, panel),
                ]);

                const actions: AnalyzerActions = result.ok && result.data ? result.data : {};
                for (const [key, select] of actionSelects) {
                    select.value = actions[key] ?? "Default";
                }
                if (overrideResult.ok && overrideResult.data) {
                    analysisOverridesLoaded = true;
                    analysisPercentInput.value = overrideResult.data.AnalysisPercent == null
                        ? ""
                        : String(overrideResult.data.AnalysisPercent);
                    analysisLengthInput.value = overrideResult.data.AnalysisLengthLimit == null
                        ? ""
                        : String(overrideResult.data.AnalysisLengthLimit);
                    previewOverrideSelect.value = overrideResult.data.PreviewFromCreditsEnd == null
                        ? "default"
                        : overrideResult.data.PreviewFromCreditsEnd
                          ? "enabled"
                          : "disabled";
                    if (configStore.isLoaded()) {
                        analysisPercentInput.placeholder = "Global: " + String(configStore.get("AnalysisPercent"));
                        analysisLengthInput.placeholder = "Global: " + String(configStore.get("AnalysisLengthLimit"));
                    }
                } else {
                    // Keep the last known values visible, but prevent Apply from
                    // turning a transient read failure into a destructive reset.
                    analysisOverridesLoaded = false;
                }
                updateApplyAvailability();
            }

            const status = await api.getScanStatus(seasonId, panel);
            if (!status.ok || !status.data) {
                return;
            }

            if (status.data.isQueued) {
                // This season's own scan is pending or running: wait for it the way a
                // click does, so the button comes back and the view refreshes when it
                // is done. Not awaited: the caller must not wait out the scan.
                scanBtn.disabled = true;
                fullSeriesCheckbox.disabled = true;
                scanBtn.textContent = "Scan in progress…";
                statusMessage.show(
                    "Scan in progress… This can take several minutes.",
                    "var(--is-text-muted)",
                );
                void awaitScan(seasonId, startScan()).then(finishScan).catch(ignoreAbort);
            } else if (status.data.isRunning) {
                // Another pass is running. It does not block a scan of this season: the
                // queue runs it once the pass ends, so the button stays enabled.
                statusMessage.show(
                    "Scan in progress… A new scan queues behind it.",
                    "var(--is-text-muted)",
                );
            }
        },
    };
}
