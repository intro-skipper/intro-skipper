import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { checkboxField } from "../components/checkbox-field.ts";
import { numberField } from "../components/number-field.ts";
import { appendTabContent } from "../components/tab-layout.ts";
import { t } from "../i18n/index.ts";

export const blackFrameTab: Tab = {
    id: "black-frame",
    getLabel: () => t("tab_blackFrame"),
    render(container) {
        appendTabContent(
            container,
            checkboxField({
                id: "UseAlternativeBlackFrameAnalyzer",
                label: t("blackFrame_altAnalyzerLabel"),
                description: t("blackFrame_altAnalyzerDesc"),
            }),
            checkboxField({
                id: "RefineCreditsBoundary",
                label: t("blackFrame_refineBoundaryLabel"),
                description: t("blackFrame_refineBoundaryDesc"),
                visible: () => configStore.get("UseAlternativeBlackFrameAnalyzer") === true,
            }),
            checkboxField({
                id: "UseChapterMarkersBlackFrame",
                label: t("blackFrame_useChapterMarkersLabel"),
                description: t("blackFrame_useChapterMarkersDesc"),
                visible: () => configStore.get("UseAlternativeBlackFrameAnalyzer") !== true,
            }),
            numberField({
                id: "BlackFrameMinimumPercentage",
                label: t("blackFrame_minPercentageLabel"),
                min: 0,
                max: 100,
                description: t("blackFrame_minPercentageDesc"),
            }),
            numberField({
                id: "BlackFrameThreshold",
                label: t("blackFrame_thresholdLabel"),
                min: 16,
                max: 255,
                description: t("blackFrame_thresholdDesc"),
            }),
        );
    },
};
