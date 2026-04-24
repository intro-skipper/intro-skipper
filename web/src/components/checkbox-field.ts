import type { CheckboxFieldOptions, PluginConfig } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { el } from "./dom.ts";
import { bindField } from "./field-bind.ts";
import { appendFieldMeta } from "./field-meta.ts";

export function checkboxField(opts: CheckboxFieldOptions): HTMLElement {
    const inputId = "field-" + opts.id;
    const container = el("div", {
        className: opts.description
            ? "checkbox-container checkbox-container-withDescription"
            : "checkbox-container",
    });

    const label = el("label", { className: "checkbox-label" });
    const input = el("input", { type: "checkbox", id: inputId, name: opts.id }) as HTMLInputElement;
    const span = el("span", {}, opts.label);
    label.append(input, span);
    container.append(label);

    const describedByIds = appendFieldMeta(container, { ...opts, idBase: inputId });

    const fieldKey = opts.id as keyof PluginConfig;

    bindField({
        container,
        input,
        fieldOpts: opts,
        describedByIds,
        onLoaded: () => {
            input.checked = configStore.get(fieldKey) as boolean;
        },
    });

    input.addEventListener("change", () => {
        configStore.set(fieldKey, input.checked);
        opts.onChange?.(input.checked);
    });

    return container;
}
