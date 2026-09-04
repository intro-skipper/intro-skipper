import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { inputField } from "../components/input-field.ts";
import { fieldGroup } from "../components/field-group.ts";

export const detectionTab: Tab = {
    id: "detection",
    label: "Detection",
    render(container) {
        const silenceVisible = () => configStore.get("AdjustIntroBasedOnSilence") === true;

        container.append(
            inputField({
                kind: "checkbox",
                id: "AdjustIntroBasedOnSilence",
                label: "Enable silence detection",
                description:
                    "When enabled, segment endpoints will be adjusted to the nearest silence point.",
            }),
            inputField({
                kind: "number",
                id: "SilenceDetectionMaximumNoise",
                label: "Noise tolerance",
                min: -90,
                max: 0,
                description: "Noise tolerance in negative decibels.",
                visible: silenceVisible,
            }),
            inputField({
                kind: "number",
                id: "SilenceDetectionMinimumDuration",
                label: "Minimum silence duration",
                min: 0,
                step: 0.01,
                description:
                    "Minimum silence duration in seconds before adjusting introduction end time.",
                visible: silenceVisible,
            }),
            inputField({
                kind: "checkbox",
                id: "SnapToKeyframe",
                label: "Enable keyframe snapping",
                description:
                    "When enabled, segment endpoints will be adjusted to the nearest video keyframe for smoother seek transitions during skipping.",
            }),
            inputField({
                kind: "checkbox",
                id: "AdjustIntroBasedOnChapters",
                label: "Enable chapter snapping",
                description:
                    "When enabled, segment start and end times will be adjusted to the nearest chapter boundary.",
            }),
            inputField({
                kind: "number",
                id: "AdjustWindowInward",
                label: "Adjustment window (inward)",
                min: 0,
                description:
                    "Maximum number of seconds to search toward a segment's interior for adjustment points (like chapter boundaries, silence, or keyframes). Used to tighten segment boundaries.",
            }),
            inputField({
                kind: "number",
                id: "AdjustWindowOutward",
                label: "Adjustment window (outward)",
                min: 0,
                description:
                    "Maximum number of seconds to search away from a segment for adjustment points (like chapter boundaries, silence, or keyframes). Used to expand segment boundaries.",
            }),
            inputField({
                kind: "number",
                id: "EndSnapThreshold",
                label: "Snap to episode start/end threshold",
                min: 0,
                description:
                    "If a segment's start or end is within this many seconds of the episode's start or end, it will be automatically adjusted (snapped) to match the episode boundary. Set to 0 to disable snapping.",
            }),
            inputField({
                kind: "checkbox",
                id: "SkipFirstEpisode",
                label: "Ignore intros for first episode of a season",
            }),
            inputField({
                kind: "checkbox",
                id: "SkipFirstEpisodeAnime",
                label: "Only ignore first episode of an anime season",
                description:
                    "If checked, the previous ignore option will only be applied to anime seasons.",
                visible: () => configStore.get("SkipFirstEpisode") === true,
            }),
            inputField({
                kind: "checkbox",
                id: "AnimePreviewFromCreditsEnd",
                label: "Set after credits scene as preview for anime",
                description:
                    "When enabled, a preview segment covering the time from the end of the credits to the end of the episode is created for anime without a detected preview.",
            }),
            fieldGroup(
                "Segment Offset Adjustment",
                inputField({
                    kind: "number",
                    id: "IntroStartOffset",
                    label: "Intro Start Offset (seconds)",
                    min: 0,
                    step: 0.5,
                    description:
                        "Default: 0. Example: If set to 3, the first 3 seconds of the intro will play before skipping.",
                }),
                inputField({
                    kind: "checkbox",
                    id: "IncludeIntroStartOffsetWhenSnapping",
                    label: "Include start offset when snapping to episode start",
                    description:
                        "When enabled, Intro Start Offset is also applied when the detected intro start is snapped to the beginning of the episode.",
                }),
                inputField({
                    kind: "number",
                    id: "IntroEndOffset",
                    label: "Intro End Offset (seconds)",
                    min: 0,
                    step: 0.5,
                    description:
                        "Default: 0. Example: If set to 3, playback will resume 3 seconds before the end of the intro.",
                }),
            ),
        );
    },
};
