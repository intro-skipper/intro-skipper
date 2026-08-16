import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { checkboxField } from "../components/checkbox-field.ts";
import { numberField } from "../components/number-field.ts";
import { appendTabContent } from "../components/tab-layout.ts";

const isLegacyAnalyzerEnabled = (): boolean =>
    configStore.get("UseLegacyBlackFrameAnalyzer") === true;

export const blackFrameTab: Tab = {
    id: "black-frame",
    label: "Black Frame",
    render(container) {
        appendTabContent(
            container,
            checkboxField({
                id: "DetectRecapUsingBlackFrames",
                label: "Detect recap using black frames",
                description:
                    "When recap chapter detection fails, mark recap from 0:00 to the latest detected black frame within the detected recap duration limits and before the intro.",
            }),
            checkboxField({
                id: "UseLegacyBlackFrameAnalyzer",
                label: "Use legacy black frame analyzer",
                description:
                    "If enabled, the legacy black frame analyzer is used instead of the modern black frame analyzer. The legacy analyzer does not support refined credits boundaries or non-black credits detection, but can use chapter markers for credits detection.",
            }),
            checkboxField({
                id: "RefineCreditsBoundary",
                label: "Refine credits boundary",
                description:
                    "Use frame-level analysis to find the exact credits boundary. Disable for faster analysis with keyframe-only accuracy.",
                visible: () => !isLegacyAnalyzerEnabled(),
            }),
            checkboxField({
                id: "DetectNonBlackCredits",
                label: "Detect non-black credits",
                description:
                    "When the black-frame scan finds nothing, also detect credits shown on a near-uniform card — text over a black, white, grey, or muted-colour background. Vivid, highly saturated backgrounds are not covered. Black-frame detection is unchanged; this only adds matches it would otherwise miss.",
                visible: () => !isLegacyAnalyzerEnabled(),
            }),
            checkboxField({
                id: "UseChapterMarkersBlackFrame",
                label: "Use chapter markers for credits detection",
                description:
                    "If enabled, chapter markers will be used to identify credits segments. Tries to detect credits by looking for black frames close to chapter markers.",
                visible: isLegacyAnalyzerEnabled,
            }),
            numberField({
                id: "BlackFrameMinimumPercentage",
                label: "Minimum percentage of black pixels",
                min: 0,
                max: 100,
                description:
                    "Minimum percentage of black pixels in a frame before it is considered a black frame. Defaults to 85.",
            }),
            numberField({
                id: "BlackFrameThreshold",
                label: "Black frame threshold",
                min: 16,
                max: 255,
                description:
                    "The threshold below which a pixel value is considered black. Defaults to 32.",
            }),
        );
    },
};
