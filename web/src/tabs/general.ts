import type { Tab } from "../types.ts";
import { MAXIMUM_SETTLED_SEASON_DELAY_HOURS } from "../config-limits.ts";
import { configStore } from "../store/config-store.ts";
import {
    clearExcludedTimestamps,
    getStorageUsage,
    injectSkipButtonCss,
} from "../store/api.ts";
import { getLibraries, getShowsInLibrary } from "../store/jellyfin-client.ts";
import { el, htmlEl } from "../components/dom.ts";
import { bindVisibility } from "../components/field-bind.ts";
import { appendTabContent } from "../components/tab-layout.ts";
import { checkboxField } from "../components/checkbox-field.ts";
import { numberField } from "../components/number-field.ts";
import { inlineCheckboxGroup } from "../components/inline-checkbox-group.ts";
import { actionButton } from "../components/action-button.ts";
import { createStatusMessage } from "../components/async-feedback.ts";
import { exclusionListField } from "../components/exclusion-list-field.ts";
import { confirmDialog } from "../components/confirm-dialog.ts";

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

function confirmDashboard(body: string, title: string): Promise<boolean> {
    return new Promise((resolve) => {
        window.Dashboard.confirm(body, title, resolve);
    });
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
    const libraries = await getLibraries();
    const groups = await Promise.all(
        libraries.map((library) => getShowsInLibrary(library.Id, library.Name)),
    );

    return groups
        .flat()
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

function countLabel(count: number, singular: string, plural: string): string {
    return `${String(count)} ${count === 1 ? singular : plural}`;
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
                statusMessage.show("Injecting CSS\u2026", "var(--is-accent)");
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
                    const msg = error instanceof Error ? error.message : "Unknown error";
                    statusMessage.show(`Failed to inject CSS: ${msg}`, "var(--is-error)");
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
                    `Cleared ${countLabel(response.data.RemovedSegments, "timestamp row", "timestamp rows")} and ${countLabel(response.data.RemovedCacheEntries, "cache row", "cache rows")} for ${countLabel(response.data.AffectedItems, "excluded item", "excluded items")}.`,
                    "var(--is-success)",
                );
            }),
            clearStatus.element,
        );

        appendTabContent(
            container,
            checkboxField({
                id: "AutoDetectIntros",
                label: "Automatically Analyze New Media",
                description:
                    'If enabled, new media will be automatically analyzed for skippable segments when added to the library<br/><br/>Note: To configure the scheduled task, see <a is="emby-linkbutton" class="button-link" href="#/dashboard/tasks">scheduled tasks</a>.',
            }),
            checkboxField({
                id: "ReanalyzeSettledSeasons",
                label: "Re-analyze settled seasons",
                description:
                    "When a season has no new episodes for the configured delay, re-analyze the whole season so segments first detected from only a few episodes are recomputed against the full season. Uses cached fingerprints, so it does not re-decode media.",
            }),
            numberField({
                id: "SettledSeasonDelayHours",
                label: "Settled season delay (hours)",
                min: 0,
                max: MAXIMUM_SETTLED_SEASON_DELAY_HOURS,
                step: 1,
                description:
                    "Treat a season as settled after this many hours without newly added episodes. Default is 24; increase this for weekly releases.",
                visible: () => configStore.get("ReanalyzeSettledSeasons") === true,
            }),
            checkboxField({
                id: "UpdateMediaSegments",
                label: "Update Missing Segments During Scan",
                description:
                    "Enable this option to update media segments for any uncached media during a library scan.<br/>This includes recently added, modified, or previously skipped (but not ignored) files.<br/><b>Warning:</b> This should be disabled if you're using media segment providers other than Intro Skipper.",
            }),
            exclusionListField({
                id: "SeriesExclusions",
                label: "Excluded series",
                placeholder: "Series name",
                description:
                    "Series names matched exactly, case-insensitively. Start typing to pick from your libraries.",
                suggestions: () => loadMediaNameSuggestions("Series"),
            }),
            exclusionListField({
                id: "MovieExclusions",
                label: "Excluded movies",
                placeholder: "Movie name",
                description:
                    "Movie names matched exactly, case-insensitively. Start typing to pick from your libraries.",
                suggestions: () => loadMediaNameSuggestions("Movie"),
            }),
            exclusionListField({
                id: "PathExclusions",
                label: "Excluded paths",
                placeholder: "/media/library",
                description:
                    "Exact paths or child paths under a listed root. Storage folders are available as suggestions when the server reports them.",
                suggestions: loadStoragePathSuggestions,
                confirmAdd: confirmPathExclusion,
            }),
            clearExcludedSection,
            inlineCheckboxGroup("Analyze for:", [
                { id: "ScanIntroduction", label: "Introduction" },
                { id: "ScanCredits", label: "Credits" },
                { id: "ScanRecap", label: "Recap" },
                { id: "ScanPreview", label: "Preview" },
                { id: "ScanCommercial", label: "Commercials" },
            ]),
            checkboxField({
                id: "AnalyzeSeasonZero",
                label: "Analyze Season 0 (Specials / Extras)",
                description:
                    "Note: Shows containing both a specials and extra folder will identify extras as season 0 and ignore specials, regardless of this setting.",
            }),
            checkboxField({
                id: "UseFileTransformationPlugin",
                label: "Use File Transformation Plugin to patch the web interface",
                disabled: () => !configStore.get("FileTransformationPluginEnabled"),
            }),
            ftWarning,
            numberField({
                id: "SkipbuttonHideDelay",
                label: "Skip button hide delay (in seconds)",
                min: 0,
                max: 1000,
                description:
                    "Time in seconds before the skip button automatically hides. Set to 0 for persistent skip button (never hides).",
                visible: () => configStore.get("UseFileTransformationPlugin") === true,
                warning:
                    "Note: This setting only applies to the web client (browsers, LG webOS, Android with web player enabled, etc). May require a refresh or clearing cache to see changes.",
            }),
            injectSection,
            checkboxField({
                id: "EnableMainMenu",
                label: "Show Intro Skipper in Main Menu",
                description:
                    "Toggle the Intro Skipper entry in the server's main navigation. Save and refresh the client (or clear cache) to apply.",
            }),
        );
    },
};
