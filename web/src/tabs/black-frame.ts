import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { inputField } from "../components/input-field.ts";

const isLegacyAnalyzerEnabled = (): boolean =>
    configStore.get("UseLegacyBlackFrameAnalyzer") === true;

export const blackFrameTab: Tab = {
    id: "black-frame",
    label: "Black Frame",
    render(container) {
        container.append(
            inputField({
                kind: "checkbox",
                id: "DetectRecapUsingBlackFrames",
                label: "Detect recap using black frames",
                description:
                    "When recap chapter detection fails, mark recap from 0:00 to the latest detected black frame within the detected recap duration limits and before the intro.",
            }),
            inputField({
                kind: "checkbox",
                id: "AnchorRecapToColdOpen",
                label: "Keep the cold open before a recap",
                description:
                    "Start a Chromaprint recap at the black frame just before the shared \"previously on\" sting instead of 0:00, so a scene played before the recap is not skipped. Chapter and black-frame-only recaps still start at 0:00. With the option above also enabled, Chromaprint must run first (Prefer Chromaprint Analysis on the Analysis tab), or the black-frame recap claims the episode at 0:00 before Chromaprint sees it.",
            }),
            inputField({
                kind: "checkbox",
                id: "RefineCreditsBoundary",
                label: "Refine credits boundary",
                description:
                    "Use frame-level analysis to find the exact credits boundary. Disable for faster analysis with keyframe-only accuracy.",
                visible: () => !isLegacyAnalyzerEnabled(),
            }),
            inputField({
                kind: "checkbox",
                id: "DetectNonBlackCredits",
                label: "Detect non-black credits",
                description:
                    "When the black-frame scan finds nothing, also detect credits shown on a near-uniform card — text over a black, white, grey, or muted-colour background. Vivid, highly saturated backgrounds are not covered. Black-frame detection is unchanged; this only adds matches it would otherwise miss.",
                visible: () => !isLegacyAnalyzerEnabled(),
            }),
            inputField({
                kind: "checkbox",
                id: "UseChapterMarkersBlackFrame",
                label: "Use chapter markers for credits detection",
                description:
                    "If enabled, chapter markers will be used to identify credits segments. Tries to detect credits by looking for black frames close to chapter markers.",
                visible: isLegacyAnalyzerEnabled,
            }),
            inputField({
                kind: "number",
                id: "BlackFrameMinimumPercentage",
                label: "Minimum percentage of black pixels",
                min: 0,
                max: 100,
                description:
                    "Minimum percentage of black pixels in a frame before it is considered a black frame. Defaults to 85.",
            }),
            inputField({
                kind: "number",
                id: "BlackFrameThreshold",
                label: "Black frame threshold",
                min: 16,
                max: 255,
                description:
                    "The threshold below which a pixel value is considered black. Defaults to 28.",
            }),
        );
    },
};
