import type { Tab } from "../types.ts";
import { MAXIMUM_SETTLED_SEASON_DELAY_HOURS } from "../config-limits.ts";
import { configStore } from "../store/config-store.ts";
import { clearExcludedTimestamps, injectSkipButtonCss } from "../store/api.ts";
import { el, htmlEl } from "../components/dom.ts";
import { bindVisibility } from "../components/field-bind.ts";
import { appendTabContent } from "../components/tab-layout.ts";
import { checkboxField } from "../components/checkbox-field.ts";
import { textField } from "../components/text-field.ts";
import { numberField } from "../components/number-field.ts";
import { inlineCheckboxGroup } from "../components/inline-checkbox-group.ts";
import { actionButton } from "../components/action-button.ts";
import { createStatusMessage } from "../components/async-feedback.ts";
import { pathBrowser } from "../components/path-browser.ts";
import { mediaExclusionCombobox } from "../components/media-exclusion-combobox.ts";
import { confirmDialog } from "../components/confirm-dialog.ts";

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

        const clearExcludedSection = el("div", { className: "input-container" });
        clearExcludedSection.append(
            el("h3", { className: "checkbox-list-label" }, "Clear Excluded Timestamps"),
        );
        clearExcludedSection.append(
            el(
                "div",
                { className: "field-description" },
                "Remove Intro Skipper timestamps and refresh Jellyfin media segments for the currently excluded series, movies, and paths. The exclusion list is not changed.",
            ),
        );

        const clearExcludedStatus = createStatusMessage();

        clearExcludedSection.append(
            actionButton("Clear Excluded Timestamps", async () => {
                const result = await confirmDialog({
                    title: "Confirm Timestamp Erasure",
                    body: "Erase Intro Skipper timestamps and refresh Jellyfin media segments for all currently excluded series, movies, and paths? The exclusion list will not change.",
                    confirmLabel: "Erase",
                });
                if (result === null) {
                    return;
                }

                clearExcludedStatus.show("Clearing excluded timestamps…", "var(--is-accent)");
                try {
                    const response = await clearExcludedTimestamps();
                    if (response.ok) {
                        clearExcludedStatus.show("Excluded timestamps cleared.", "var(--is-success)");
                    } else {
                        clearExcludedStatus.show(
                            `Failed to clear excluded timestamps: Server returned ${String(response.status)}`,
                            "var(--is-error)",
                        );
                    }
                } catch (error: unknown) {
                    const msg = error instanceof Error ? error.message : "Unknown error";
                    clearExcludedStatus.show(
                        `Failed to clear excluded timestamps: ${msg}`,
                        "var(--is-error)",
                    );
                }
            }),
        );
        clearExcludedSection.append(clearExcludedStatus.element);

        const ftWarning = htmlEl(
            "div",
            { className: "field-warning" },
            "<strong>File Transformation Plugin Required</strong><br/>" +
                "This feature requires the File Transformation plugin to work. " +
                '<a href="https://github.com/IAmParadox27/jellyfin-plugin-file-transformation" target="_blank">Install it here</a>',
        );
        bindVisibility(ftWarning, () => !configStore.get("FileTransformationPluginEnabled"));

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
            mediaExclusionCombobox(),
            clearExcludedSection,
            textField({
                id: "ExcludePaths",
                label: "Exclude paths",
                description:
                    "Exclude media from analysis by file path. Any file whose full path contains one fragment (case-insensitive) is skipped. Useful for excluding remote or cloud-mounted directories (e.g. Real-Debrid/Zurg) from fingerprinting.",
            }),
            pathBrowser(),
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
