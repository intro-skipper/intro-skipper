import { el } from "./dom.ts";
import { bindStatusMessage, withDashboardLoading } from "./async-feedback.ts";
import { confirmDialog } from "./confirm-dialog.ts";
import * as api from "../store/api.ts";
import type { AnalysisOverrides, AnalyzerActions, PluginConfig, SeasonItem } from "../types.ts";
import { delay } from "../utils.ts";
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
    prepareForShow: (showId: string) => void;
    loadForSeason: (
        showId: string,
        seasonId: string,
        isMovie: boolean,
        seriesSeasons?: readonly SeasonItem[],
    ) => Promise<void>;
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

    const analysisPercentInput = overrideField(
        "ts-analysis-percent-override",
        "Percent of media to analyze",
        "Percentage of each item's runtime.",
        "1",
        "50",
    );
    const analysisLengthInput = overrideField(
        "ts-analysis-length-override",
        "Maximum runtime to analyze (minutes)",
        "Upper limit for each item.",
        "1",
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
    const handleConfigLoaded = () => updatePreviewDefaultLabel();
    const handleConfigChanged = ({ field }: { field: keyof PluginConfig }) => {
        if (field === "AnimePreviewFromCreditsEnd") updatePreviewDefaultLabel();
    };
    configStore.subscribe("loaded", handleConfigLoaded);
    configStore.subscribe("changed", handleConfigChanged);
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
    const handleResetWindowClick = () => {
        analysisPercentInput.value = "";
        analysisLengthInput.value = "";
        previewOverrideSelect.value = "default";
    };
    resetWindowBtn.addEventListener("click", handleResetWindowClick);
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
        "Segment Editor \u2192",
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
    let destroyed = false;
    let loadVersion = 0;
    let scanVersion = 0;
    let analysisOverridesLoaded = false;

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

    fullSeriesCheckbox.addEventListener("change", updateActionLabels);

    const handleApplyClick = async () => {
        if (destroyed || !analysisOverridesLoaded) return;

        const operationLoadVersion = loadVersion;
        const seasonIds = targetSeasonIds();
        const actions: AnalyzerActions = {};
        for (const [key, select] of actionSelects) {
            actions[key] = select.value;
        }

        let overrides: AnalysisOverrides;
        try {
            overrides = {
                AnalysisPercent: readOverride(analysisPercentInput, "Percent", 1, 50),
                AnalysisLengthLimit: readOverride(analysisLengthInput, "Maximum runtime", 1),
                PreviewFromCreditsEnd:
                    previewOverrideSelect.value === "default"
                        ? null
                        : previewOverrideSelect.value === "enabled",
            };
        } catch (err) {
            statusMessage.show(err instanceof Error ? err.message : "Invalid analysis overrides.", "var(--is-error)");
            return;
        }

        statusMessage.show("Saving overrides\u2026", "var(--is-text-muted)");

        try {
            const completed = await withDashboardLoading(async () => {
                for (const seasonId of seasonIds) {
                    if (destroyed || loadVersion !== operationLoadVersion) {
                        return false;
                    }

                    const response = await api.updateAnalyzerActions(seasonId, actions);
                    if (destroyed || loadVersion !== operationLoadVersion) {
                        return false;
                    }
                    if (!response.ok) {
                        throw new Error("Failed to update analyzer overrides");
                    }
                    const windowResponse = await api.updateAnalysisOverrides(seasonId, overrides);
                    if (!windowResponse.ok) {
                        throw new Error("Failed to update analysis window");
                    }
                }
                return true;
            });
            if (!completed || destroyed || loadVersion !== operationLoadVersion) return;
            statusMessage.show("Overrides updated.", "var(--is-success)");
        } catch {
            statusMessage.show("Failed to update overrides.", "var(--is-error)");
        }
    };

    // Waits for the season's queued scan: "completed", "failed", or "stopped" when the
    // wait was cut short by navigation, teardown or an unavailable status. The scan may
    // wait behind a running pass for as long as that pass takes, so only repeated
    // status failures stop the wait early.
    const awaitScan = async (
        scanToken: number,
        seasonId: string,
    ): Promise<"completed" | "failed" | "stopped"> => {
        const MAX_FAILURES = 30;
        const BASE_INTERVAL = 1000;
        const MAX_INTERVAL = 10_000;

        let failures = 0;
        let interval = BASE_INTERVAL;

        while (!destroyed && scanToken === scanVersion) {
            await delay(interval);
            if (destroyed || scanToken !== scanVersion) return "stopped";

            const status = await api.getScanStatus(seasonId);
            if (destroyed || scanToken !== scanVersion) return "stopped";

            if (status.ok && status.data) {
                if (!status.data.isQueued) return status.data.failed ? "failed" : "completed";
                failures = 0;
                interval = BASE_INTERVAL;
                continue;
            }

            failures++;
            if (failures >= MAX_FAILURES) {
                resetScanButton();
                statusMessage.show(
                    "Scan status unavailable. Refresh to check results.",
                    "var(--is-warning)",
                );
                return "stopped";
            }
            interval = Math.min(interval * 2, MAX_INTERVAL);
        }

        return "stopped";
    };

    // Ends the scan flow once its last scan settled. The onScanComplete callback owns
    // the refresh (and may withhold it over unsaved edits), so the message must not
    // claim one.
    const finishScan = async (outcome: "completed" | "failed") => {
        resetScanButton();
        if (outcome === "failed") {
            statusMessage.show("Scan failed. See the server log.", "var(--is-error)");
        } else {
            statusMessage.show("Scan finished.", "var(--is-success)");
        }
        await Promise.resolve(opts.onScanComplete());
    };

    const handleScanClick = async () => {
        if (destroyed) return;

        const scanToken = ++scanVersion;
        scanBtn.disabled = true;
        fullSeriesCheckbox.disabled = true;
        const showId = currentShowId;
        const seasonIds = targetSeasonIds();
        const isSeriesScan = !currentIsMovie && fullSeriesCheckbox.checked;
        statusMessage.show("Starting scan\u2026", "var(--is-text-muted)");
        try {
            for (let index = 0; index < seasonIds.length; index++) {
                if (destroyed || scanToken !== scanVersion) return;

                const progress = isSeriesScan
                    ? " (" + String(index + 1) + "/" + String(seasonIds.length) + ")"
                    : "";
                statusMessage.show("Starting scan" + progress + "\u2026", "var(--is-text-muted)");
                const response = await withDashboardLoading(() =>
                    api.scanSeason(showId, seasonIds[index]),
                );

                if (destroyed || scanToken !== scanVersion) return;

                if (!response.ok) {
                    resetScanButton();
                    statusMessage.show("Unable to start the scan.", "var(--is-error)");
                    return;
                }

                scanBtn.textContent = "Scan in progress\u2026";
                statusMessage.show(
                    (isSeriesScan ? "Scanning series" + progress : "Scan queued") +
                        "\u2026 This can take several minutes.",
                    "var(--is-text-muted)",
                );

                const outcome = await awaitScan(scanToken, seasonIds[index]);
                if (outcome === "stopped") return;
                if (outcome === "failed") {
                    await finishScan("failed");
                    return;
                }
            }

            await finishScan("completed");
        } catch {
            resetScanButton();
            statusMessage.show("Unable to start the scan.", "var(--is-error)");
        }
    };

    const handleEraseClick = async () => {
        if (destroyed) return;

        const isSeriesErase = !currentIsMovie && fullSeriesCheckbox.checked;
        const label = currentIsMovie ? "movie" : isSeriesErase ? "series" : "season";
        const showId = currentShowId;
        const seasonIds = targetSeasonIds();
        const result = await confirmDialog({
            title: "Confirm Timestamp Erasure",
            body: "Are you sure you want to erase all timestamps for this " + label + "?",
            confirmLabel: "Erase",
            checkbox: { label: "Include cached fingerprints" },
        });
        if (destroyed) return;
        if (!result) return;
        eraseBtn.disabled = true;
        fullSeriesCheckbox.disabled = true;
        statusMessage.show("Erasing timestamps\u2026", "var(--is-text-muted)");
        try {
            for (const seasonId of seasonIds) {
                const response = await api.eraseItemTimestamps(
                    seasonUrl(showId, seasonId),
                    result.checkboxChecked,
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
            await Promise.resolve(opts.onScanComplete());
        } catch {
            statusMessage.show("Failed to erase timestamps.", "var(--is-error)");
        } finally {
            eraseBtn.disabled = false;
            fullSeriesCheckbox.disabled = false;
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

        prepareForShow(showId: string) {
            if (destroyed) return;

            loadVersion += 1;
            scanVersion += 1;
            currentShowId = showId;
            currentSeasonId = "";
            currentIsMovie = false;
            currentSeriesSeasons = [];
            fullSeriesCheckbox.checked = false;
            analysisOverridesLoaded = false;
            fullSeriesLabel.style.display = "";
            resetScanButton();
            updateApplyAvailability();
            statusMessage.clear();
        },

        async loadForSeason(
            showId: string,
            seasonId: string,
            isMovie: boolean,
            seriesSeasons?: readonly SeasonItem[],
        ) {
            if (destroyed) return;

            const loadToken = ++loadVersion;
            scanVersion += 1;
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
                    api.getAnalyzerActions(seasonId),
                    api.getAnalysisOverrides(seasonId),
                ]);
                if (destroyed || loadToken !== loadVersion) {
                    return;
                }

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

            const status = await api.getScanStatus(seasonId);
            if (destroyed || loadToken !== loadVersion || !status.ok || !status.data) {
                return;
            }

            if (status.data.isQueued) {
                // This season's own scan is pending or running: wait for it the way a
                // click does, so the button comes back and the view refreshes when it
                // is done. Not awaited: the caller must not wait out the scan.
                scanBtn.disabled = true;
                fullSeriesCheckbox.disabled = true;
                scanBtn.textContent = "Scan in progress\u2026";
                statusMessage.show(
                    "Scan in progress\u2026 This can take several minutes.",
                    "var(--is-text-muted)",
                );
                const scanToken = scanVersion;
                void awaitScan(scanToken, seasonId)
                    .then((outcome) => (outcome === "stopped" ? undefined : finishScan(outcome)))
                    .catch(console.error);
            } else if (status.data.isRunning) {
                // Another pass is running. It does not block a scan of this season: the
                // queue runs it once the pass ends, so the button stays enabled.
                statusMessage.show(
                    "Scan in progress\u2026 A new scan queues behind it.",
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
            fullSeriesCheckbox.removeEventListener("change", updateActionLabels);
            resetWindowBtn.removeEventListener("click", handleResetWindowClick);
            configStore.unsubscribe("loaded", handleConfigLoaded);
            configStore.unsubscribe("changed", handleConfigChanged);
        },
    };
}
