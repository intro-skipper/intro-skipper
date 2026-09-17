import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import {
    clearExcludedTimestamps,
    getStorageUsage,
    injectSkipButtonCss,
} from "../store/api.ts";
import { getAllShows } from "../store/jellyfin-client.ts";
import { el, htmlEl } from "../components/dom.ts";
import { bindVisibility } from "../components/field-bind.ts";
import { configField } from "../components/input-field.ts";
import { inlineCheckboxGroup } from "../components/inline-checkbox-group.ts";
import { actionButton } from "../components/action-button.ts";
import { createStatusMessage } from "../components/async-feedback.ts";
import { exclusionListField } from "../components/exclusion-list-field.ts";
import { confirmDashboard, confirmDialog } from "../components/confirm-dialog.ts";
import { errorText, pluralize } from "../utils.ts";

function normalizePathCandidate(value: string): string {
    let normalized = value.trim().replace(/\\/g, "/");
    while (normalized.length > 1 && normalized.endsWith("/")) {
        normalized = normalized.slice(0, -1);
    }

    return normalized;
}

function isBroadPathRoot(value: string): boolean {
    const normalized = normalizePathCandidate(value);
    if (normalized === "/" || /^[A-Za-z]:$/.test(normalized)) {
        return true;
    }

    return normalized.startsWith("//") && normalized.split("/").filter(Boolean).length === 2;
}

async function confirmPathExclusion(value: string): Promise<boolean> {
    if (!isBroadPathRoot(value)) {
        return true;
    }

    return confirmDashboard(
        "This path appears to be a filesystem root or drive root. Excluding it can skip a large part of the library.",
        "Confirm Path Exclusion",
    );
}

async function loadMediaNameSuggestions(type: "Series" | "Movie"): Promise<string[]> {
    return (await getAllShows())
        .filter((item) => item.Type === type)
        .map((item) => item.Name)
        .sort((a, b) => a.localeCompare(b));
}

async function loadStoragePathSuggestions(): Promise<string[]> {
    const libraries = await getStorageUsage();
    const paths: string[] = [];
    for (const library of libraries) {
        for (const folder of library.Folders) {
            const path = folder.Path.trim();
            if (path.length > 0) {
                paths.push(path);
            }
        }
    }

    return paths;
}

export const generalTab: Tab = {
    id: "general",
    label: "General",
    render(container) {
        const injectSection = el("div", { className: "input-container" });
        injectSection.append(
            el("h3", { className: "checkbox-list-label" }, "Inject Skip Button CSS"),
        );
        injectSection.append(
            el(
                "div",
                { className: "field-description" },
                "Inject CSS to load skip button styles into your Jellyfin branding setting using an @import statement.",
            ),
        );

        const statusMessage = createStatusMessage();

        injectSection.append(
            actionButton("Inject CSS", async () => {
                statusMessage.show("Injecting CSS…", "var(--is-accent)");
                try {
                    const response = await injectSkipButtonCss();
                    if (response.ok) {
                        statusMessage.show(
                            "Skip button CSS injected successfully!",
                            "var(--is-success)",
                        );
                    } else {
                        statusMessage.show(
                            `Failed to inject CSS: Server returned ${String(response.status)}`,
                            "var(--is-error)",
                        );
                    }
                } catch (error: unknown) {
                    statusMessage.show(`Failed to inject CSS: ${errorText(error)}`, "var(--is-error)");
                }
            }),
        );
        injectSection.append(statusMessage.element);

        const ftWarning = htmlEl(
            "div",
            { className: "field-warning" },
            "<strong>File Transformation Plugin Required</strong><br/>" +
                "This feature requires the File Transformation plugin to work. " +
                '<a href="https://github.com/IAmParadox27/jellyfin-plugin-file-transformation" target="_blank">Install it here</a>',
        );
        bindVisibility(ftWarning, () => !configStore.get("FileTransformationPluginEnabled"));

        const clearExcludedSection = el("div", { className: "input-container" });
        clearExcludedSection.append(
            el("h3", { className: "checkbox-list-label" }, "Clear Excluded Timestamps"),
            el(
                "div",
                { className: "field-description" },
                "Remove timestamp, cache, and season-state rows for media currently matched by the exclusion lists.",
            ),
        );

        const clearStatus = createStatusMessage({ display: "block" });
        clearExcludedSection.append(
            actionButton("Clear excluded timestamp data", async () => {
                if (configStore.isDirty()) {
                    clearStatus.show(
                        "Save configuration changes before clearing timestamp data.",
                        "var(--is-error)",
                    );
                    return;
                }

                const result = await confirmDialog({
                    title: "Clear Excluded Timestamps",
                    body: "Remove timestamp data for media currently matched by the exclusion lists. Included media in the same seasons will be kept.",
                    confirmLabel: "Clear",
                });
                if (!result) return;

                clearStatus.show("Clearing excluded timestamp data...", "var(--is-accent)");
                const response = await clearExcludedTimestamps();
                if (!response.ok || !response.data) {
                    clearStatus.show(
                        response.error ?? "Failed to clear excluded timestamp data.",
                        "var(--is-error)",
                    );
                    return;
                }

                clearStatus.show(
                    `Cleared ${pluralize(response.data.RemovedSegments, "timestamp row", "timestamp rows")} and ${pluralize(response.data.RemovedCacheEntries, "cache row", "cache rows")} for ${pluralize(response.data.AffectedItems, "excluded item", "excluded items")}.`,
                    "var(--is-success)",
                );
            }),
            clearStatus.element,
        );

        const fileTransformationOn = () =>
            configStore.get("UseFileTransformationPlugin") === true;

        container.append(
            configField("AutoDetectIntros"),
            configField("ReanalyzeSettledSeasons"),
            configField("SettledSeasonDelayHours", {
                visible: () => configStore.get("ReanalyzeSettledSeasons") === true,
            }),
            configField("UpdateMediaSegments"),
            exclusionListField("SeriesExclusions", {
                suggestions: () => loadMediaNameSuggestions("Series"),
            }),
            exclusionListField("MovieExclusions", {
                suggestions: () => loadMediaNameSuggestions("Movie"),
            }),
            exclusionListField("PathExclusions", {
                suggestions: loadStoragePathSuggestions,
                confirmAdd: confirmPathExclusion,
            }),
            clearExcludedSection,
            inlineCheckboxGroup("Analyze for:", [
                "ScanIntroduction",
                "ScanCredits",
                "ScanRecap",
                "ScanPreview",
                "ScanCommercial",
            ]),
            configField("AnalyzeSeasonZero"),
            configField("UseFileTransformationPlugin", {
                disabled: () => !configStore.get("FileTransformationPluginEnabled"),
            }),
            ftWarning,
            configField("SkipbuttonHideDelay", { visible: fileTransformationOn }),
            configField("AutoSkipIntro", { visible: fileTransformationOn }),
            configField("AutoSkipCredits", { visible: fileTransformationOn }),
            configField("SkipButtonVisibleSeconds", { visible: fileTransformationOn }),
            injectSection,
            configField("EnableMainMenu"),
        );
    },
};
