import type { Tab } from "../types.ts";
import { configForm } from "../components/config-form.ts";
import { patternField } from "../components/pattern-field.ts";

export const subtitlesTab: Tab = {
    id: "subtitles",
    label: "Subtitles",
    render(container, signal) {
        const form = configForm(signal);

        container.append(
            form.field("EnableSubtitleRecapDetection"),
            patternField(form, "SubtitleRecapPattern"),
            form.field("EnableSubtitlePreviewDetection"),
            patternField(form, "SubtitlePreviewPattern"),
            form.field("SubtitleLanguages"),
        );
    },
};
