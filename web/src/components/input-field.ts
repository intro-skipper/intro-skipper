import {
    labelText,
    type FieldKind,
    type FieldSchema,
    type FieldSpec,
    type FieldStore,
    type KeysOfKind,
    type ValuesOf,
} from "../config/field-spec.ts";
import { el } from "./dom.ts";
import { bindField, type BoundStore } from "./field-bind.ts";
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

/** Rules that depend on other settings, so they stay with the tab that renders the field. */
export type FieldRules = {
    visible?: () => boolean;
    disabled?: () => boolean;
};

/** What a control needs beyond its schema entry: its rules and the lifetime its subscriptions end with. */
export type FieldOptions = FieldRules & { signal: AbortSignal };

type ControlKind = Exclude<FieldKind, "list">;

/** Every key of `S` that fieldControl can render. Lists have exclusionListField. */
export type ControlKey<S extends FieldSchema> = KeysOfKind<S, ControlKind>;

/**
 * A store a form binds to: typed against its schema's values at the call site,
 * and read through string keys inside the controls.
 */
export type FormStore<S extends FieldSchema> = FieldStore<ValuesOf<S>> & BoundStore;

/**
 * A form control bound to `store[id]`, built from the field's entry in `schema`:
 * label, control, optional error line, and the description/warning meta. The
 * control reads and writes the store by the kind it rendered.
 */
export function fieldControl<S extends FieldSchema>(
    schema: S,
    store: FormStore<S>,
    id: ControlKey<S>,
    options: FieldOptions,
): HTMLElement {
    const spec: FieldSpec = schema[id];
    const key = String(id);
    switch (spec.kind) {
        case "checkbox":
            return checkbox(store, key, spec, options);
        case "select":
            return select(store, key, spec, options);
        case "list":
            throw new Error(`${key} is a list; render it with exclusionListField`);
        default:
            return textInput(store, key, spec, options);
    }
}

/** The controls of one form, all bound to the same store and ending with the same signal. */
export function formFor<S extends FieldSchema>(schema: S, store: FormStore<S>, signal: AbortSignal) {
    return {
        field(id: ControlKey<S>, rules: FieldRules = {}): HTMLElement {
            return fieldControl(schema, store, id, { ...rules, signal });
        },
    };
}

function checkbox(
    store: BoundStore,
    id: string,
    spec: Extract<FieldSpec, { kind: "checkbox" }>,
    options: FieldOptions,
): HTMLElement {
    const inputId = "field-" + id;
    const container = el("div", {
        className: spec.description
            ? "checkbox-container checkbox-container-withDescription"
            : "checkbox-container",
    });
    const input = el("input", { type: "checkbox", id: inputId, name: id });
    container.append(el("label", { className: "checkbox-label" }, input, el("span", {}, spec.label)));

    bindField({
        store,
        container,
        input,
        id,
        ...options,
        describedByIds: appendFieldMeta(container, { ...spec, idBase: inputId }),
        onLoaded: () => {
            input.checked = store.get(id) === true;
        },
    });
    input.addEventListener("change", () => store.set(id, input.checked));
    return container;
}

function select(
    store: BoundStore,
    id: string,
    spec: Extract<FieldSpec, { kind: "select" }>,
    options: FieldOptions,
): HTMLElement {
    const inputId = "field-" + id;
    const container = el("div", { className: "select-container" });
    const label = el("label", { className: "select-label", for: inputId }, spec.label);
    const control = el("select", { id: inputId, name: id });
    // Options are addressed by index so a choice's value can be any primitive,
    // including null for "use the default".
    const optionEls = spec.options.map((choice, index) =>
        el("option", { value: String(index) }, labelText(choice.label)),
    );
    control.append(...optionEls);
    container.append(label, control);

    bindField({
        store,
        container,
        input: control,
        id,
        ...options,
        describedByIds: appendFieldMeta(container, { ...spec, idBase: inputId }),
        onLoaded: () => {
            spec.options.forEach((choice, index) => {
                if (typeof choice.label === "function") optionEls[index].textContent = choice.label();
            });
            const current = store.get(id);
            const index = spec.options.findIndex((choice) => choice.value === current);
            control.value = index >= 0 ? String(index) : "";
        },
    });
    control.addEventListener("change", () => {
        const choice = spec.options[Number(control.value)];
        if (choice) store.set(id, choice.value);
    });
    return container;
}

function textInput(
    store: BoundStore,
    id: string,
    spec: Extract<FieldSpec, { kind: "number" | "text" | "regex" }>,
    options: FieldOptions,
): HTMLElement {
    const inputId = "field-" + id;
    const container = el("div", { className: "input-container" });
    const label = el("label", { className: "input-label", for: inputId }, spec.label);
    const inputAttrs: Record<string, string> = {
        type: spec.kind === "number" ? "number" : "text",
        id: inputId,
        name: id,
        autocomplete: "off",
    };
    let description = spec.description;
    if (spec.kind === "number") {
        const { min, max, step } = spec;
        inputAttrs.inputmode = step !== undefined && String(step).includes(".") ? "decimal" : "numeric";
        if (min !== undefined) inputAttrs.min = String(min);
        if (max !== undefined) inputAttrs.max = String(max);
        if (step !== undefined) inputAttrs.step = String(step);
    } else if (spec.kind === "regex") {
        inputAttrs.placeholder = spec.default;
        description = (description ?? "") + " <br/>Default: <code>" + spec.default + "</code>";
    } else if (spec.placeholder) {
        inputAttrs.placeholder = spec.placeholder;
    }
    const input = el("input", inputAttrs);
    const errorDiv = el("div", { className: "field-error" });
    container.append(label, input, errorDiv);

    bindField({
        store,
        container,
        input,
        id,
        ...options,
        errorDiv,
        describedByIds: appendFieldMeta(container, {
            description,
            warning: spec.warning,
            idBase: inputId,
        }),
        onLoaded: () => {
            if (spec.kind === "number" && spec.placeholder !== undefined) {
                input.placeholder = labelText(spec.placeholder);
            }
            const value = store.get(id);
            input.value = value === null || value === undefined ? "" : String(value);
        },
    });

    const commit =
        spec.kind === "number"
            ? () => {
                  if (input.value === "") {
                      // Empty is "no value" for an optional field; otherwise the
                      // user is still typing.
                      if (spec.optional) store.set(id, null);
                      return;
                  }
                  const num = Number(input.value);
                  if (Number.isNaN(num)) return;
                  store.set(id, num);
              }
            : () => store.set(id, input.value);
    input.addEventListener("input", debounced(commit));

    return container;
}
