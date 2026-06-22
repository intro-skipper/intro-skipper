import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { checkboxField } from "../components/checkbox-field.ts";
import { numberField } from "../components/number-field.ts";
import { appendTabContent } from "../components/tab-layout.ts";

export const blackFrameTab: Tab = {
    id: "black-frame",
    label: "Black Frame",
    render(container) {
        appendTabContent(
            container,
            checkboxField({
                id: "UseAlternativeBlackFrameAnalyzer",
                label: "Use alternative black frame analyzer (experimental)",
                description:
                    "If enabled, the alternative black frame analyzer will be used. This analyzer is experimental and may not work as expected.",
            }),
            checkboxField({
                id: "RefineCreditsBoundary",
                label: "Refine credits boundary",
                description:
                    "Use frame-level analysis to find the exact credits boundary. Disable for faster analysis with keyframe-only accuracy.",
                visible: () => configStore.get("UseAlternativeBlackFrameAnalyzer") === true,
            }),
            checkboxField({
                id: "ThoroughBlackIntervalScan",
                label: "Thorough black interval scan",
                description:
                    "Decode every frame during the credits black interval scan instead of reference frames only. Improves accuracy for very short black intervals at the cost of slower analysis.",
                visible: () => configStore.get("UseAlternativeBlackFrameAnalyzer") === true,
            }),
            checkboxField({
                id: "UseChapterMarkersBlackFrame",
                label: "Use chapter markers for credits detection",
                description:
                    "If enabled, chapter markers will be used to identify credits segments. Tries to detect credits by looking for black frames close to chapter markers.",
                visible: () => configStore.get("UseAlternativeBlackFrameAnalyzer") !== true,
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
