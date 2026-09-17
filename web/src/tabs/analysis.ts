import type { Tab } from "../types.ts";
import type { KeyOfKind } from "../config/schema.ts";
import { configStore } from "../store/config-store.ts";
import { htmlEl } from "../components/dom.ts";
import { bindVisibility } from "../components/field-bind.ts";
import { fieldRow } from "../components/tab-layout.ts";
import { configField } from "../components/input-field.ts";

/** A min/max pair side by side. The ordering rule itself lives in the schema. */
function pairRow(
    minId: KeyOfKind<"number">,
    maxId: KeyOfKind<"number">,
    visible?: () => boolean,
): HTMLElement {
    const row = fieldRow(configField(minId), configField(maxId));
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

        container.append(
            configField("PreferChromaprint"),
            configField("EnhanceChapterCredits"),
            configField("FullLengthChapters"),
            configField("AnalysisPercent"),
            configField("AnalysisLengthLimit"),
            info,
            pairRow("MinimumRecapDuration", "MaximumRecapDuration"),
            pairRow("MinimumRecapDetectionDuration", "MaximumRecapDetectionDuration"),
            pairRow("MinimumIntroDuration", "MaximumIntroDuration"),
            pairRow("MinimumCreditsDuration", "MaximumCreditsDuration"),
            configField("MaximumMovieCreditsDuration"),
            pairRow("MinimumPreviewDuration", "MaximumPreviewDuration", chaptersOff),
            pairRow("MinimumCommercialDuration", "MaximumCommercialDuration", chaptersOff),
        );
    },
};
