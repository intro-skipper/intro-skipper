import type { InputFieldOptions } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { el } from "./dom.ts";
import { bindField } from "./field-bind.ts";
import { appendFieldMeta } from "./field-meta.ts";

/** Delay before committing typed input to the store (ms). */
const INPUT_DEBOUNCE_MS = 180;

function debounced(fn: () => void): () => void {
    let timer: ReturnType<typeof setTimeout> | null = null;
    return () => {
        if (timer) clearTimeout(timer);
        timer = setTimeout(fn, INPUT_DEBOUNCE_MS);
    };
}

/**
 * A config-bound form control: label, control, optional error line, and the
 * description/warning meta. The control reads from and writes to configStore
 * under `opts.id`; the `kind` decides the element and which keys are accepted.
 */
export function inputField(opts: InputFieldOptions): HTMLElement {
    const inputId = "field-" + opts.id;

    if (opts.kind === "checkbox") {
        const container = el("div", {
            className: opts.description
                ? "checkbox-container checkbox-container-withDescription"
                : "checkbox-container",
        });
        const input = el("input", { type: "checkbox", id: inputId, name: opts.id });
        container.append(el("label", { className: "checkbox-label" }, input, el("span", {}, opts.label)));

        bindField({
            container,
            input,
            fieldOpts: opts,
            describedByIds: appendFieldMeta(container, { ...opts, idBase: inputId }),
            onLoaded: () => {
                input.checked = configStore.get(opts.id);
            },
        });
        input.addEventListener("change", () => configStore.set(opts.id, input.checked));
        return container;
    }

    if (opts.kind === "select") {
        const container = el("div", { className: "select-container" });
        const label = el("label", { className: "select-label", for: inputId }, opts.label);
        const select = el("select", { id: inputId, name: opts.id });
        for (const opt of opts.options) {
            select.append(el("option", { value: opt.value }, opt.label));
        }
        container.append(label, select);

        bindField({
            container,
            input: select,
            fieldOpts: opts,
            describedByIds: appendFieldMeta(container, { ...opts, idBase: inputId }),
            onLoaded: () => {
                select.value = configStore.get(opts.id);
            },
        });
        select.addEventListener("change", () => configStore.set(opts.id, select.value));
        return container;
    }

    const container = el("div", { className: "input-container" });
    const label = el("label", { className: "input-label", for: inputId }, opts.label);
    const inputAttrs: Record<string, string> = {
        type: opts.kind,
        id: inputId,
        name: opts.id,
        autocomplete: "off",
    };
    if (opts.kind === "number") {
        inputAttrs.inputmode =
            opts.step !== undefined && String(opts.step).includes(".") ? "decimal" : "numeric";
        if (opts.min !== undefined) inputAttrs.min = String(opts.min);
        if (opts.max !== undefined) inputAttrs.max = String(opts.max);
        if (opts.step !== undefined) inputAttrs.step = String(opts.step);
    } else if (opts.placeholder) {
        inputAttrs.placeholder = opts.placeholder;
    }
    const input = el("input", inputAttrs);
    const errorDiv = el("div", { className: "field-error" });
    container.append(label, input, errorDiv);

    bindField({
        container,
        input,
        fieldOpts: opts,
        errorDiv,
        describedByIds: appendFieldMeta(container, { ...opts, idBase: inputId }),
        onLoaded: () => {
            input.value = String(configStore.get(opts.id));
        },
    });

    const commit =
        opts.kind === "number"
            ? () => {
                  // Empty or non-numeric text means the user is still typing.
                  if (input.value === "") return;
                  const num = Number(input.value);
                  if (Number.isNaN(num)) return;
                  configStore.set(opts.id, num);
              }
            : () => configStore.set(opts.id, input.value);
    input.addEventListener("input", debounced(commit));

    return container;
}
