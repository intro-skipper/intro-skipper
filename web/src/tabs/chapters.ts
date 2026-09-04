import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { el } from "../components/dom.ts";
import { inputField } from "../components/input-field.ts";

const DEFAULTS = {
    ChapterAnalyzerIntroductionPattern: "(^|\\s)(Intro|Introduction|OP|Opening)(?![\\s:]+End)(\\s|:|$)",
    ChapterAnalyzerEndCreditsPattern: "(^|\\s)(Credits?|ED|Ending|Outro)(?![\\s:]+End)(\\s|:|$)",
    ChapterAnalyzerPreviewPattern:
        "(^|\\s)(Preview|PV|Sneak\\s?Peek|Coming\\s?(Up|Soon)|Next\\s+(time|on|episode)|Extra|Teaser|Trailer)(?![\\s:]+End)(\\s|:|$)",
    ChapterAnalyzerRecapPattern:
        "(^|\\s)(Re?cap|Sum{1,2}ary|Prev(ious(ly)?)?|(Last|Earlier)(\\s\\w+)?|Catch[ -]up)(?![\\s:]+End)(\\s|:|$)",
    ChapterAnalyzerCommercialPattern: "(^|\\s)(Ad(vert(isement)?)?|Commercial|Intermission)(?![\\s:]+End)(\\s|:|$)",
};

function patternField(id: keyof typeof DEFAULTS, label: string, typeNoun: string): HTMLElement {
    const wrapper = el("div", { className: "pattern-field" });
    const defaultPattern = DEFAULTS[id];

    wrapper.append(
        inputField({
            kind: "text",
            id,
            label,
            placeholder: defaultPattern,
            description:
                "Enter a regular expression to detect " +
                typeNoun +
                " chapters. <br/>Default: <code>" +
                defaultPattern +
                "</code>",
        }),
    );

    const resetBtn = el(
        "button",
        { className: "action-button reset-button", type: "button" },
        "Reset to default",
    );
    resetBtn.addEventListener("click", () => {
        configStore.set(id, DEFAULTS[id]);
    });
    wrapper.append(resetBtn);

    return wrapper;
}

export const chaptersTab: Tab = {
    id: "chapters",
    label: "Chapters",
    render(container) {
        container.append(
            patternField("ChapterAnalyzerIntroductionPattern", "Introductions", "introduction"),
            patternField("ChapterAnalyzerEndCreditsPattern", "Credits", "credits"),
            patternField("ChapterAnalyzerPreviewPattern", "Preview", "preview"),
            patternField("ChapterAnalyzerRecapPattern", "Recaps", "recap"),
            patternField("ChapterAnalyzerCommercialPattern", "Commercials", "commercial"),
            inputField({
                kind: "checkbox",
                id: "EnableSponsorBlockChapterDetection",
                label: "Enable SponsorBlock chapter detection",
                description:
                    "Detect known SponsorBlock chapter labels in addition to the regular expressions above.",
            }),
        );
    },
};
