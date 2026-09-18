import type { Tab } from "../types.ts";
import { configField } from "../components/input-field.ts";

export const blackFrameTab: Tab = {
    id: "black-frame",
    label: "Black Frame",
    render(container) {
        container.append(
            configField("DetectRecapUsingBlackFrames"),
            configField("AnchorRecapToColdOpen"),
            configField("RefineCreditsBoundary"),
            configField("DetectNonBlackCredits"),
            configField("BlackFrameMinimumPercentage"),
            configField("BlackFrameThreshold"),
        );
    },
};
