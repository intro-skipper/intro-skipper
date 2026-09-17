import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { configField } from "../components/input-field.ts";

const legacyAnalyzer = (): boolean => configStore.get("UseLegacyBlackFrameAnalyzer") === true;
const modernAnalyzer = (): boolean => !legacyAnalyzer();

export const blackFrameTab: Tab = {
    id: "black-frame",
    label: "Black Frame",
    render(container) {
        container.append(
            configField("DetectRecapUsingBlackFrames"),
            configField("AnchorRecapToColdOpen"),
            configField("RefineCreditsBoundary", { visible: modernAnalyzer }),
            configField("DetectNonBlackCredits", { visible: modernAnalyzer }),
            configField("UseChapterMarkersBlackFrame", { visible: legacyAnalyzer }),
            configField("BlackFrameMinimumPercentage"),
            configField("BlackFrameThreshold"),
        );
    },
};
