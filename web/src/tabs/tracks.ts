import type { Tab } from "../types.ts";
import { configForm } from "../components/config-form.ts";
import { el } from "../components/dom.ts";
import { patternField } from "../components/pattern-field.ts";

export const tracksTab: Tab = {
    id: "tracks",
    label: "Tracks",
    render(container, signal) {
        const form = configForm(signal);

        container.append(
            el("h3", { className: "tracks-section-divider" }, "Audio"),
            form.field("PreferredAudioLanguage"),
            form.field("PreferAudioStreamWithMostChannels"),
            form.field("ProbeAudioDuration"),
            el("h3", { className: "tracks-section-divider" }, "Subtitle"),
            form.field("SubtitleLanguages"),
            patternField(form, "SubtitleRecapPattern"),
            patternField(form, "SubtitlePreviewPattern"),
        );
    },
};
