import { el } from "./dom.ts";

export function fieldGroup(label: string, ...children: HTMLElement[]): HTMLElement {
    const fieldset = el("fieldset");
    const legend = el("legend", {}, label);
    fieldset.append(legend);
    for (const child of children) {
        fieldset.append(child);
    }
    return fieldset;
}
