import type { Tab } from "../types.ts";
import { configForm } from "../components/config-form.ts";
import { patternField } from "../components/pattern-field.ts";

export const chaptersTab: Tab = {
    id: "chapters",
    label: "Chapters",
    render(container, signal) {
        const form = configForm(signal);

        container.append(
            patternField(form, "ChapterAnalyzerIntroductionPattern"),
            patternField(form, "ChapterAnalyzerEndCreditsPattern"),
            patternField(form, "ChapterAnalyzerPreviewPattern"),
            patternField(form, "ChapterAnalyzerRecapPattern"),
            patternField(form, "ChapterAnalyzerCommercialPattern"),
            form.field("EnableSponsorBlockChapterDetection"),
        );
    },
};
