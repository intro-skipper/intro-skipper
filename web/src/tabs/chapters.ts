import type { Tab } from "../types.ts";
import { configSchema, type KeyOfKind } from "../config/schema.ts";
import { configStore } from "../store/config-store.ts";
import { el } from "../components/dom.ts";
import { configForm, type ConfigForm } from "../components/config-form.ts";

/** A regex field with a button that writes the schema default back. */
function patternField(form: ConfigForm, id: KeyOfKind<"regex">): HTMLElement {
    const wrapper = el("div", { className: "pattern-field" });
    const resetBtn = el(
        "button",
        { className: "action-button reset-button", type: "button" },
        "Reset to default",
    );
    resetBtn.addEventListener("click", () => {
        configStore.set(id, configSchema[id].default);
    });
    wrapper.append(form.field(id), resetBtn);
    return wrapper;
}

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
