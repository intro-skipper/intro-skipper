import type { TextFieldOptions, PluginConfig } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { el } from "./dom.ts";
import { bindField } from "./field-bind.ts";
import { appendFieldMeta } from "./field-meta.ts";

/** Delay before committing an input value to the store (ms). */
const INPUT_DEBOUNCE_MS = 180;

export function textField(opts: TextFieldOptions): HTMLElement {
    const container = el("div", { className: "input-container" });
    const inputId = "field-" + opts.id;

    const label = el("label", { className: "input-label" }, opts.label);
    label.setAttribute("for", inputId);
    container.append(label);

    const inputAttrs: Record<string, string> = {
        type: "text",
        id: inputId,
        name: opts.id,
        autocomplete: "off",
    };
    if (opts.placeholder) inputAttrs.placeholder = opts.placeholder;

    const input = el("input", inputAttrs) as HTMLInputElement;
    container.append(input);

    const errorDiv = el("div", { className: "field-error" });
    container.append(errorDiv);

    const describedByIds = appendFieldMeta(container, { ...opts, idBase: inputId });

    const fieldKey = opts.id as keyof PluginConfig;

    bindField({
        container,
        input,
        fieldOpts: opts,
        errorDiv,
        describedByIds,
        onLoaded: () => {
            input.value = String(configStore.get(fieldKey));
        },
    });

    let debounceTimer: ReturnType<typeof setTimeout> | null = null;
    input.addEventListener("input", () => {
        if (debounceTimer) clearTimeout(debounceTimer);
        debounceTimer = setTimeout(() => {
            configStore.set(fieldKey, input.value);
        }, INPUT_DEBOUNCE_MS);
    });

    return container;
}
