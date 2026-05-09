import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { checkboxField } from "../components/checkbox-field.ts";
import { numberField } from "../components/number-field.ts";
import { fieldGroup } from "../components/field-group.ts";
import { appendTabContent } from "../components/tab-layout.ts";
import { t } from "../i18n/index.ts";

export const detectionTab: Tab = {
    id: "detection",
    label: () => t("tab_detection"),
    render(container) {
        const silenceVisible = () => configStore.get("AdjustIntroBasedOnSilence") === true;

        appendTabContent(
            container,
            checkboxField({
                id: "AdjustIntroBasedOnSilence",
                label: t("detection_silenceLabel"),
                description: t("detection_silenceDesc"),
            }),
            numberField({
                id: "SilenceDetectionMaximumNoise",
                label: t("detection_noiseLabel"),
                min: -90,
                max: 0,
                description: t("detection_noiseDesc"),
                visible: silenceVisible,
            }),
            numberField({
                id: "SilenceDetectionMinimumDuration",
                label: t("detection_minSilenceLabel"),
                min: 0,
                step: 0.01,
                description: t("detection_minSilenceDesc"),
                visible: silenceVisible,
            }),
            checkboxField({
                id: "SnapToKeyframe",
                label: t("detection_keyframeLabel"),
                description: t("detection_keyframeDesc"),
            }),
            checkboxField({
                id: "AdjustIntroBasedOnChapters",
                label: t("detection_chapterSnapLabel"),
                description: t("detection_chapterSnapDesc"),
            }),
            numberField({
                id: "AdjustWindowInward",
                label: t("detection_adjustWindowInwardLabel"),
                min: 0,
                description: t("detection_adjustWindowInwardDesc"),
            }),
            numberField({
                id: "AdjustWindowOutward",
                label: t("detection_adjustWindowOutwardLabel"),
                min: 0,
                description: t("detection_adjustWindowOutwardDesc"),
            }),
            numberField({
                id: "EndSnapThreshold",
                label: t("detection_endSnapThresholdLabel"),
                min: 0,
                description: t("detection_endSnapThresholdDesc"),
            }),
            checkboxField({
                id: "SkipFirstEpisode",
                label: t("detection_skipFirstEpisodeLabel"),
            }),
            checkboxField({
                id: "SkipFirstEpisodeAnime",
                label: t("detection_skipFirstAnimeLabel"),
                description: t("detection_skipFirstAnimeDesc"),
                visible: () => configStore.get("SkipFirstEpisode") === true,
            }),
            checkboxField({
                id: "AnimePreviewFromCreditsEnd",
                label: t("detection_animePreviewLabel"),
                description: t("detection_animePreviewDesc"),
            }),
            fieldGroup(
                t("detection_segmentOffsetTitle"),
                numberField({
                    id: "IntroStartOffset",
                    label: t("detection_introStartOffsetLabel"),
                    min: 0,
                    step: 0.5,
                    description: t("detection_introStartOffsetDesc"),
                }),
                numberField({
                    id: "IntroEndOffset",
                    label: t("detection_introEndOffsetLabel"),
                    min: 0,
                    step: 0.5,
                    description: t("detection_introEndOffsetDesc"),
                }),
            ),
        );
    },
};
