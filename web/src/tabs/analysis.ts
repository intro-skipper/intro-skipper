import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { htmlEl } from "../components/dom.ts";
import { bindVisibility } from "../components/field-bind.ts";
import { appendTabContent, fieldRow } from "../components/tab-layout.ts";
import { tabWarning } from "../components/tab-warning.ts";
import { checkboxField } from "../components/checkbox-field.ts";
import { numberField } from "../components/number-field.ts";
import { MAXIMUM_ANALYSIS_PERCENT, MINIMUM_ANALYSIS_PERCENT } from "../config-limits.ts";

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
    label: "Analysis",
    render(container) {
        const info = htmlEl(
            "div",
            { className: "field-description" },
            "<p>The amount of each item's content that will be analyzed is determined using the percentage and maximum runtime. The minimum of (duration &times; percent, maximum runtime) is the amount that will be analyzed.</p>" +
                "<p>If the percentage or maximum runtime settings are modified, the cached fingerprints and timestamps for each series, season, or movie you want to analyze with the modified settings <b>will have to be recreated</b>.</p>" +
                "<p>Increasing either of the above settings will cause episode analysis to take much longer.</p>",
        );

        const chaptersOff = () => configStore.get("FullLengthChapters") !== true;

        appendTabContent(
            container,
            tabWarning(
                "Changes here require regenerating media segments to take effect. Per the MediaSegments API, records are updated individually and may be slow.",
            ),
            checkboxField({
                id: "PreferChromaprint",
                label: "Prefer Chromaprint Analysis",
                description:
                    "Only use chromaprint for analysis, unless it is not available. Setting an analysis mode in the advanced options will override this setting.",
            }),
            checkboxField({
                id: "FullLengthChapters",
                label: "Ignore duration limits for chapters",
                description:
                    "Allow segments to extend to the end of a chapter when the marker exceeds other user settings, such as percentage or duration.",
            }),
            numberField({
                id: "AnalysisPercent",
                label: "Percent of media to analyze",
                min: MINIMUM_ANALYSIS_PERCENT,
                max: MAXIMUM_ANALYSIS_PERCENT,
                description:
                    "Analysis will be limited to this percentage of each item's runtime. For example, a value of 25 (the default) will limit analysis to the first quarter of each item.",
            }),
            numberField({
                id: "AnalysisLengthLimit",
                label: "Maximum runtime to analyze (in minutes)",
                min: 1,
                description:
                    "Analysis will be limited to this amount of each item's runtime. For example, a value of 10 (the default) will limit analysis to the first 10 minutes of each item.",
            }),
            info,
            durationPair(
                "MinimumRecapDuration",
                "MaximumRecapDuration",
                "Minimum recap duration (in seconds)",
                "Maximum recap duration (in seconds)",
                "Recap chapters which are shorter than this duration will not be considered a recap.",
                "Recap chapters which are longer than this duration will not be considered a recap.",
                chaptersOff,
            ),
            durationPair(
                "MinimumRecapDetectionDuration",
                "MaximumRecapDetectionDuration",
                "Minimum detected recap duration (in seconds)",
                "Maximum detected recap duration (in seconds)",
                "Blackframe/chromaprint recaps shorter than this duration will not be detected.",
                "Blackframe/chromaprint recaps longer than this duration will be capped or ignored.",
                chaptersOff,
            ),
            checkboxField({
                id: "DetectRecapUsingFirstBlackFrame",
                label: "Detect recap using black frames",
                description:
                    "When recap chapter detection fails, mark recap from 0:00 to the latest detected black frame within the detected recap duration limits and before the intro.",
                visible: chaptersOff,
            }),
            durationPair(
                "MinimumIntroDuration",
                "MaximumIntroDuration",
                "Minimum introduction duration (in seconds)",
                "Maximum introduction duration (in seconds)",
                "Segments or similar sounding audio which is shorter than this duration will not be considered an introduction.",
                "Segments or similar sounding audio which is longer than this duration will not be considered an introduction.",
            ),
            durationPair(
                "MinimumCreditsDuration",
                "MaximumCreditsDuration",
                "Minimum credits duration (in seconds)",
                "Maximum credits duration (in seconds)",
                "Segments or similar sounding audio which is shorter than this duration will not be considered credits.",
                "Segments or similar sounding audio which is longer than this duration will not be considered credits.",
            ),
            numberField({
                id: "MaximumMovieCreditsDuration",
                label: "Maximum movie credits duration (in seconds)",
                min: 1,
                description:
                    "Segments longer than this duration will not be considered movie credits.",
            }),
            durationPair(
                "MinimumPreviewDuration",
                "MaximumPreviewDuration",
                "Minimum preview duration (in seconds)",
                "Maximum preview duration (in seconds)",
                "Segments which are shorter than this duration will not be considered a preview.",
                "Segments which are longer than this duration will not be considered a preview.",
                chaptersOff,
            ),
            durationPair(
                "MinimumCommercialDuration",
                "MaximumCommercialDuration",
                "Minimum commercial duration (in seconds)",
                "Maximum commercial duration (in seconds)",
                "Segments which are shorter than this duration will not be considered a commercial.",
                "Segments which are longer than this duration will not be considered a commercial.",
                chaptersOff,
            ),
        );
    },
};
