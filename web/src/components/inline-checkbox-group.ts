import { configSchema, type KeyOfKind } from "../config/schema.ts";
import { configStore } from "../store/config-store.ts";
import { el } from "./dom.ts";
import { bindField } from "./field-bind.ts";

/** A row of checkbox fields under one legend; labels come from the schema. */
export function inlineCheckboxGroup(
    title: string,
    ids: readonly KeyOfKind<"checkbox">[],
    signal: AbortSignal,
): HTMLElement {
    const container = el("fieldset", {
        className: "checkbox-container analyze-for analyze-for-group",
    });

    const titleLegend = el("legend", { className: "title analyze-for-legend" }, title);
    container.append(titleLegend);

    for (const id of ids) {
        const inputId = "field-" + id;
        const label = el("label", { className: "checkbox-label", for: inputId });
        const input = el("input", { type: "checkbox", id: inputId, name: id });
        const span = el("span", {}, configSchema[id].label);
        label.append(input, span);
        container.append(label);

        bindField({
            container: label,
            input,
            id,
            signal,
            onLoaded: () => {
                input.checked = configStore.get(id);
            },
        });

        input.addEventListener("change", () => {
            configStore.set(id, input.checked);
        });
    }

    return container;
}
