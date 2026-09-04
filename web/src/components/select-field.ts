import type { SelectFieldOptions, PluginConfig } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { el } from "./dom.ts";
import { bindField } from "./field-bind.ts";
import { appendFieldMeta } from "./field-meta.ts";

export function selectField(opts: SelectFieldOptions): HTMLElement {
    const container = el("div", { className: "select-container" });
    const inputId = "field-" + opts.id;

    const label = el("label", { className: "select-label" }, opts.label);
    label.setAttribute("for", inputId);
    container.append(label);

    const select = el("select", { id: inputId, name: opts.id }) as HTMLSelectElement;
    for (const opt of opts.options) {
        const option = el("option", { value: opt.value }, opt.label);
        select.append(option);
    }
    container.append(select);

    const describedByIds = appendFieldMeta(container, { ...opts, idBase: inputId });

    const fieldKey = opts.id as keyof PluginConfig;

    bindField({
        container,
        input: select,
        fieldOpts: opts,
        describedByIds,
        onLoaded: () => {
            select.value = String(configStore.get(fieldKey));
        },
    });

    select.addEventListener("change", () => {
        configStore.set(fieldKey, select.value);
    });

    return container;
}
