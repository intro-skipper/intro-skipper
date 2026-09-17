import {
    configSchema,
    type FieldKind,
    type FieldSpec,
    type KeyOfKind,
} from "../config/schema.ts";
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

/** Rules that depend on other settings, so they stay with the tab that renders the field. */
export type FieldOverrides = {
    visible?: () => boolean;
    disabled?: () => boolean;
};

type ControlKind = Exclude<FieldKind, "list">;

/** Every config key configField can render. Lists have exclusionListField. */
export type ControlKey = KeyOfKind<ControlKind>;

// A key with its own spec, so narrowing on `kind` narrows both together.
type Field = {
    [Kd in ControlKind]: { kind: Kd; id: KeyOfKind<Kd>; spec: Extract<FieldSpec, { kind: Kd }> };
}[ControlKind];

/**
 * A config-bound form control: label, control, optional error line, and the
 * description/warning meta, all read from the field's schema entry. The control
 * reads from and writes to configStore under `id`.
 */
export function configField(id: ControlKey, overrides: FieldOverrides = {}): HTMLElement {
    const spec: FieldSpec = configSchema[id];
    // The one cast: TypeScript cannot tell that the entry indexed by `id` is the
    // entry whose kind we just read.
    const field = { kind: spec.kind, id, spec } as Field;
    const inputId = "field-" + id;

    switch (field.kind) {
        case "checkbox":
            return checkbox(field, inputId, overrides);
        case "select":
            return select(field, inputId, overrides);
        default:
            return textInput(field, inputId, overrides);
    }
}

function checkbox(
    { id, spec }: Extract<Field, { kind: "checkbox" }>,
    inputId: string,
    overrides: FieldOverrides,
): HTMLElement {
    const container = el("div", {
        className: spec.description
            ? "checkbox-container checkbox-container-withDescription"
            : "checkbox-container",
    });
    const input = el("input", { type: "checkbox", id: inputId, name: id });
    container.append(el("label", { className: "checkbox-label" }, input, el("span", {}, spec.label)));

    bindField({
        container,
        input,
        fieldOpts: { id, ...overrides },
        describedByIds: appendFieldMeta(container, { ...spec, idBase: inputId }),
        onLoaded: () => {
            input.checked = configStore.get(id);
        },
    });
    input.addEventListener("change", () => configStore.set(id, input.checked));
    return container;
}

function select(
    { id, spec }: Extract<Field, { kind: "select" }>,
    inputId: string,
    overrides: FieldOverrides,
): HTMLElement {
    const container = el("div", { className: "select-container" });
    const label = el("label", { className: "select-label", for: inputId }, spec.label);
    const control = el("select", { id: inputId, name: id });
    // Read the options from the schema entry itself so their values keep the
    // literal types the store expects.
    const options = configSchema[id].options;
    for (const option of options) {
        control.append(el("option", { value: option.value }, option.label));
    }
    container.append(label, control);

    bindField({
        container,
        input: control,
        fieldOpts: { id, ...overrides },
        describedByIds: appendFieldMeta(container, { ...spec, idBase: inputId }),
        onLoaded: () => {
            control.value = configStore.get(id);
        },
    });
    control.addEventListener("change", () => {
        const chosen = options.find((option) => option.value === control.value);
        if (chosen) configStore.set(id, chosen.value);
    });
    return container;
}

function textInput(
    field: Extract<Field, { kind: "number" | "text" | "regex" }>,
    inputId: string,
    overrides: FieldOverrides,
): HTMLElement {
    const { id, spec } = field;
    const container = el("div", { className: "input-container" });
    const label = el("label", { className: "input-label", for: inputId }, spec.label);
    const inputAttrs: Record<string, string> = {
        type: field.kind === "number" ? "number" : "text",
        id: inputId,
        name: id,
        autocomplete: "off",
    };
    let description = spec.description;
    if (field.kind === "number") {
        const { min, max, step } = field.spec;
        inputAttrs.inputmode = step !== undefined && String(step).includes(".") ? "decimal" : "numeric";
        if (min !== undefined) inputAttrs.min = String(min);
        if (max !== undefined) inputAttrs.max = String(max);
        if (step !== undefined) inputAttrs.step = String(step);
    } else if (field.kind === "regex") {
        inputAttrs.placeholder = field.spec.default;
        description =
            (description ?? "") + " <br/>Default: <code>" + field.spec.default + "</code>";
    } else if (field.spec.placeholder) {
        inputAttrs.placeholder = field.spec.placeholder;
    }
    const input = el("input", inputAttrs);
    const errorDiv = el("div", { className: "field-error" });
    container.append(label, input, errorDiv);

    bindField({
        container,
        input,
        fieldOpts: { id, ...overrides },
        errorDiv,
        describedByIds: appendFieldMeta(container, {
            description,
            warning: spec.warning,
            idBase: inputId,
        }),
        onLoaded: () => {
            input.value = String(configStore.get(id));
        },
    });

    const commit =
        field.kind === "number"
            ? () => {
                  // Empty or non-numeric text means the user is still typing.
                  if (input.value === "") return;
                  const num = Number(input.value);
                  if (Number.isNaN(num)) return;
                  configStore.set(field.id, num);
              }
            : () => configStore.set(field.id, input.value);
    input.addEventListener("input", debounced(commit));

    return container;
}
