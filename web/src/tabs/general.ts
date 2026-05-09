import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { injectSkipButtonCss } from "../store/api.ts";
import { el, htmlEl } from "../components/dom.ts";
import { bindVisibility } from "../components/field-bind.ts";
import { appendTabContent } from "../components/tab-layout.ts";
import { checkboxField } from "../components/checkbox-field.ts";
import { textField } from "../components/text-field.ts";
import { numberField } from "../components/number-field.ts";
import { inlineCheckboxGroup } from "../components/inline-checkbox-group.ts";
import { actionButton } from "../components/action-button.ts";
import { createStatusMessage } from "../components/async-feedback.ts";
import { t } from "../i18n/index.ts";

export const generalTab: Tab = {
    id: "general",
    label: () => t("tab_general"),
    render(container) {
        const injectSection = el("div", { className: "input-container" });
        injectSection.append(
            el("h3", { className: "checkbox-list-label" }, t("general_injectCssTitle")),
        );
        injectSection.append(
            el(
                "div",
                { className: "field-description" },
                t("general_injectCssDesc"),
            ),
        );

        const statusMessage = createStatusMessage();

        injectSection.append(
            actionButton(t("general_injectCssButton"), async () => {
                statusMessage.show(t("general_injectingCss"), "var(--is-accent)");
                try {
                    const response = await injectSkipButtonCss();
                    if (response.ok) {
                        statusMessage.show(
                            t("general_injectCssSuccess"),
                            "var(--is-success)",
                        );
                    } else {
                        statusMessage.show(
                            t("general_injectCssFailedStatus", { status: String(response.status) }),
                            "var(--is-error)",
                        );
                    }
                } catch (error: unknown) {
                    const msg = error instanceof Error ? error.message : "Unknown error";
                    statusMessage.show(t("general_injectCssFailedMsg", { msg }), "var(--is-error)");
                }
            }),
        );
        injectSection.append(statusMessage.element);

        const ftWarning = htmlEl(
            "div",
            { className: "field-warning" },
            t("general_ftWarning"),
        );
        bindVisibility(ftWarning, () => !configStore.get("FileTransformationPluginEnabled"));

        appendTabContent(
            container,
            checkboxField({
                id: "AutoDetectIntros",
                label: t("general_autoAnalyzeLabel"),
                description: t("general_autoAnalyzeDesc"),
            }),
            checkboxField({
                id: "UpdateMediaSegments",
                label: t("general_updateSegmentsLabel"),
                description: t("general_updateSegmentsDesc"),
            }),
            textField({
                id: "ExcludeSeries",
                label: t("general_excludeSeriesLabel"),
                description: t("general_excludeSeriesDesc"),
            }),
            inlineCheckboxGroup(t("general_analyzeForLabel"), [
                { id: "ScanIntroduction", label: t("general_segIntroduction") },
                { id: "ScanCredits", label: t("general_segCredits") },
                { id: "ScanRecap", label: t("general_segRecap") },
                { id: "ScanPreview", label: t("general_segPreview") },
                { id: "ScanCommercial", label: t("general_segCommercials") },
            ]),
            checkboxField({
                id: "AnalyzeSeasonZero",
                label: t("general_analyzeSeasonZeroLabel"),
                description: t("general_analyzeSeasonZeroDesc"),
            }),
            checkboxField({
                id: "UseFileTransformationPlugin",
                label: t("general_useFileTransformLabel"),
                disabled: () => !configStore.get("FileTransformationPluginEnabled"),
            }),
            ftWarning,
            numberField({
                id: "SkipbuttonHideDelay",
                label: t("general_skipButtonDelayLabel"),
                min: 0,
                max: 1000,
                description: t("general_skipButtonDelayDesc"),
                visible: () => configStore.get("UseFileTransformationPlugin") === true,
                warning: t("general_skipButtonDelayWarning"),
            }),
            injectSection,
            checkboxField({
                id: "EnableMainMenu",
                label: t("general_enableMainMenuLabel"),
                description: t("general_enableMainMenuDesc"),
            }),
        );
    },
};
