import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { configField } from "../components/input-field.ts";
import { fieldGroup } from "../components/field-group.ts";

export const detectionTab: Tab = {
    id: "detection",
    label: "Detection",
    render(container) {
        const silenceVisible = () => configStore.get("AdjustIntroBasedOnSilence") === true;

        container.append(
            configField("AdjustIntroBasedOnSilence"),
            configField("SilenceDetectionMaximumNoise", { visible: silenceVisible }),
            configField("SilenceDetectionMinimumDuration", { visible: silenceVisible }),
            configField("SnapToKeyframe"),
            configField("AdjustIntroBasedOnChapters"),
            configField("AdjustWindowInward"),
            configField("AdjustWindowOutward"),
            configField("EndSnapThreshold"),
            configField("FirstEpisodeIntroMode"),
            configField("AnimePreviewFromCreditsEnd"),
            fieldGroup(
                "Segment Offset Adjustment",
                configField("IntroStartOffset"),
                configField("IncludeIntroStartOffsetWhenSnapping"),
                configField("IntroEndOffset"),
            ),
        );
    },
};
