import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { htmlEl } from "../components/dom.ts";
import { bindVisibility } from "../components/field-bind.ts";
import { appendTabContent, fieldRow } from "../components/tab-layout.ts";
import { tabWarning } from "../components/tab-warning.ts";
import { checkboxField } from "../components/checkbox-field.ts";
import { numberField } from "../components/number-field.ts";
import { MAXIMUM_ANALYSIS_PERCENT, MINIMUM_ANALYSIS_PERCENT } from "../config-limits.ts";
import { t } from "../i18n/index.ts";

function durationPair(
    minId: string,
    maxId: string,
    minLabel: string,
    maxLabel: string,
    minDesc: string,
    maxDesc: string,
    visible?: () => boolean,
): HTMLElement {
    const row = fieldRow(
        numberField({ id: minId, label: minLabel, min: 1, description: minDesc }),
        numberField({ id: maxId, label: maxLabel, min: 1, description: maxDesc }),
    );
    bindVisibility(row, visible);
    return row;
}

export const analysisTab: Tab = {
    id: "analysis",
    label: () => t("tab_analysis"),
    render(container) {
        const info = htmlEl(
            "div",
            { className: "field-description" },
            t("analysis_info"),
        );

        const chaptersOff = () => configStore.get("FullLengthChapters") !== true;

        appendTabContent(
            container,
            tabWarning(t("analysis_warning")),
            checkboxField({
                id: "PreferChromaprint",
                label: t("analysis_preferChromaprintLabel"),
                description: t("analysis_preferChromaprintDesc"),
            }),
            checkboxField({
                id: "FullLengthChapters",
                label: t("analysis_fullLengthChaptersLabel"),
                description: t("analysis_fullLengthChaptersDesc"),
            }),
            numberField({
                id: "AnalysisPercent",
                label: t("analysis_percentLabel"),
                min: MINIMUM_ANALYSIS_PERCENT,
                max: MAXIMUM_ANALYSIS_PERCENT,
                description: t("analysis_percentDesc"),
            }),
            numberField({
                id: "AnalysisLengthLimit",
                label: t("analysis_maxRuntimeLabel"),
                min: 1,
                description: t("analysis_maxRuntimeDesc"),
            }),
            info,
            durationPair(
                "MinimumIntroDuration",
                "MaximumIntroDuration",
                t("analysis_minIntroDurationLabel"),
                t("analysis_maxIntroDurationLabel"),
                t("analysis_minIntroDurationDesc"),
                t("analysis_maxIntroDurationDesc"),
            ),
            durationPair(
                "MinimumCreditsDuration",
                "MaximumCreditsDuration",
                t("analysis_minCreditsDurationLabel"),
                t("analysis_maxCreditsDurationLabel"),
                t("analysis_minCreditsDurationDesc"),
                t("analysis_maxCreditsDurationDesc"),
            ),
            numberField({
                id: "MaximumMovieCreditsDuration",
                label: t("analysis_maxMovieCreditsDurationLabel"),
                min: 1,
                description: t("analysis_maxMovieCreditsDurationDesc"),
            }),
            durationPair(
                "MinimumRecapDuration",
                "MaximumRecapDuration",
                t("analysis_minRecapDurationLabel"),
                t("analysis_maxRecapDurationLabel"),
                t("analysis_minRecapDurationDesc"),
                t("analysis_maxRecapDurationDesc"),
                chaptersOff,
            ),
            durationPair(
                "MinimumPreviewDuration",
                "MaximumPreviewDuration",
                t("analysis_minPreviewDurationLabel"),
                t("analysis_maxPreviewDurationLabel"),
                t("analysis_minPreviewDurationDesc"),
                t("analysis_maxPreviewDurationDesc"),
                chaptersOff,
            ),
            durationPair(
                "MinimumCommercialDuration",
                "MaximumCommercialDuration",
                t("analysis_minCommercialDurationLabel"),
                t("analysis_maxCommercialDurationLabel"),
                t("analysis_minCommercialDurationDesc"),
                t("analysis_maxCommercialDurationDesc"),
                chaptersOff,
            ),
        );
    },
};
