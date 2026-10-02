import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { configForm } from "../components/config-form.ts";
import { el } from "../components/dom.ts";

export const detectionTab: Tab = {
    id: "detection",
    label: "Detection",
    render(container, signal) {
        const form = configForm(signal);
        const silenceVisible = () => configStore.get("AdjustIntroBasedOnSilence") === true;

        container.append(
            form.field("AdjustIntroBasedOnSilence"),
            form.field("SilenceDetectionMaximumNoise", { visible: silenceVisible }),
            form.field("SilenceDetectionMinimumDuration", { visible: silenceVisible }),
            form.field("SnapToKeyframe"),
            form.field("AdjustIntroBasedOnChapters"),
            form.field("AdjustWindowInward"),
            form.field("AdjustWindowOutward"),
            form.field("EndSnapThreshold"),
            form.field("FirstEpisodeIntroMode"),
            form.field("AnimePreviewFromCreditsEnd"),
            el(
                "fieldset",
                {},
                el("legend", {}, "Segment Offset Adjustment"),
                form.field("IntroStartOffset"),
                form.field("IncludeIntroStartOffsetWhenSnapping"),
                form.field("IntroEndOffset"),
            ),
        );
    },
};
