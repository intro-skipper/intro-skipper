import type { Tab } from "../types.ts";
import { configSchema, type KeyOfKind } from "../config/schema.ts";
import { configStore } from "../store/config-store.ts";
import { el } from "../components/dom.ts";
import { configField } from "../components/input-field.ts";

/** A regex field with a button that writes the schema default back. */
function patternField(id: KeyOfKind<"regex">): HTMLElement {
    const wrapper = el("div", { className: "pattern-field" });
    const resetBtn = el(
        "button",
        { className: "action-button reset-button", type: "button" },
        "Reset to default",
    );
    resetBtn.addEventListener("click", () => {
        configStore.set(id, configSchema[id].default);
    });
    wrapper.append(configField(id), resetBtn);
    return wrapper;
}

export const chaptersTab: Tab = {
    id: "chapters",
    label: "Chapters",
    render(container) {
        container.append(
            patternField("ChapterAnalyzerIntroductionPattern"),
            patternField("ChapterAnalyzerEndCreditsPattern"),
            patternField("ChapterAnalyzerPreviewPattern"),
            patternField("ChapterAnalyzerRecapPattern"),
            patternField("ChapterAnalyzerCommercialPattern"),
            configField("EnableSponsorBlockChapterDetection"),
        );
    },
};
