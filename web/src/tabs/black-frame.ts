import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { configForm } from "../components/config-form.ts";

const legacyAnalyzer = (): boolean => configStore.get("UseLegacyBlackFrameAnalyzer") === true;
const modernAnalyzer = (): boolean => !legacyAnalyzer();

export const blackFrameTab: Tab = {
    id: "black-frame",
    label: "Black Frame",
    render(container, signal) {
        const form = configForm(signal);

        container.append(
            form.field("DetectRecapUsingBlackFrames"),
            form.field("AnchorRecapToColdOpen"),
            form.field("RefineCreditsBoundary", { visible: modernAnalyzer }),
            form.field("DetectNonBlackCredits", { visible: modernAnalyzer }),
            form.field("UseChapterMarkersBlackFrame", { visible: legacyAnalyzer }),
            form.field("BlackFrameMinimumPercentage"),
            form.field("BlackFrameThreshold"),
        );
    },
};
