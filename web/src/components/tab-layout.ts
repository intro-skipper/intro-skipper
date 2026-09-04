import { el } from "./dom.ts";

export function fieldRow(...children: HTMLElement[]): HTMLElement {
    const row = el("div", { className: "side-by-side" });
    row.append(...children);
    return row;
}
