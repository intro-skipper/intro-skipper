import type { Tab, PluginConfig } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { el } from "../components/dom.ts";
import { appendTabContent } from "../components/tab-layout.ts";
import { textField } from "../components/text-field.ts";

const DEFAULTS: Record<string, string> = {
  ChapterAnalyzerIntroductionPattern: "(^|\\s)(Intro|Introduction|OP|Opening)(?!\\sEnd)(\\s|$)",
  ChapterAnalyzerEndCreditsPattern: "(^|\\s)(Credits?|ED|Ending|Outro)(?!\\sEnd)(\\s|$)",
  ChapterAnalyzerPreviewPattern:
    "(^|\\s)(Preview|PV|Sneak\\s?Peek|Coming\\s?(Up|Soon)|Next\\s+(time|on|episode)|Extra|Teaser|Trailer)(?!\\sEnd)(\\s|:|$)",
  ChapterAnalyzerRecapPattern:
    "(^|\\s)(Re?cap|Sum{1,2}ary|Prev(ious(ly)?)?|(Last|Earlier)(\\s\\w+)?|Catch[ -]up)(?!\\sEnd)(\\s|:|$)",
  ChapterAnalyzerCommercialPattern: "(^|\\s)(Ad(vert(isement)?)?|Commercial)(?!\\sEnd)(\\s|$)",
};

function patternField(id: string, label: string, typeNoun: string): HTMLElement {
  const wrapper = el("div", { className: "pattern-field" });
  const defaultPattern = DEFAULTS[id];

  wrapper.append(
    textField({
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
    configStore.set(id as keyof PluginConfig, DEFAULTS[id]);
  });
  wrapper.append(resetBtn);

  return wrapper;
}

export const chaptersTab: Tab = {
  id: "chapters",
  label: "Chapters",
  render(container) {
    appendTabContent(
      container,
      patternField("ChapterAnalyzerIntroductionPattern", "Introductions", "introduction"),
      patternField("ChapterAnalyzerEndCreditsPattern", "Credits", "credits"),
      patternField("ChapterAnalyzerPreviewPattern", "Preview", "preview"),
      patternField("ChapterAnalyzerRecapPattern", "Recaps", "recap"),
      patternField("ChapterAnalyzerCommercialPattern", "Commercials", "commercial"),
    );
  },
};
