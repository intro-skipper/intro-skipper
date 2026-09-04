import type { ConfigKeysOfType } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { el } from "./dom.ts";
import { bindField } from "./field-bind.ts";

export function inlineCheckboxGroup(
    title: string,
    items: Array<{ id: ConfigKeysOfType<boolean>; label: string }>,
): HTMLElement {
    const container = el("fieldset", {
        className: "checkbox-container analyze-for analyze-for-group",
    });

    const titleLegend = el("legend", { className: "title analyze-for-legend" }, title);
    container.append(titleLegend);

    for (const item of items) {
        const inputId = "field-" + item.id;
        const label = el("label", { className: "checkbox-label", for: inputId });
        const input = el("input", { type: "checkbox", id: inputId, name: item.id });
        const span = el("span", {}, item.label);
        label.append(input, span);
        container.append(label);

        bindField({
            container: label,
            input,
            fieldOpts: item,
            onLoaded: () => {
                input.checked = configStore.get(item.id);
            },
        });

        input.addEventListener("change", () => {
            configStore.set(item.id, input.checked);
        });
    }

    return container;
}
