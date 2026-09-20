import type { FieldStore } from "../config/field-spec.ts";

/** Any field store, seen through string keys. What a rendered control binds to. */
export type BoundStore = FieldStore<Record<string, unknown>>;

/** Adds ids to the control's aria-describedby without dropping existing ones. */
export function setDescribedBy(input: HTMLInputElement | HTMLSelectElement, ids: string[]): void {
    if (ids.length === 0) {
        return;
    }

    const describedBy = new Set(
        (input.getAttribute("aria-describedby") ?? "").split(/\s+/).filter(Boolean),
    );

    for (const id of ids) {
        describedBy.add(id);
    }

    input.setAttribute("aria-describedby", Array.from(describedBy).join(" "));
}

/**
 * Shared wiring for a control bound to `store[id]`: initial value, visibility,
 * disabled state, and validation messages, all until `signal` aborts. `onLoaded`
 * copies the store value into the control; it is skipped while the control has
 * focus so typing is not clobbered.
 */
export function bindField(opts: {
    store: BoundStore;
    container: HTMLElement;
    input: HTMLInputElement | HTMLSelectElement;
    id: string;
    signal: AbortSignal;
    disabled?: () => boolean;
    visible?: () => boolean;
    errorDiv?: HTMLElement;
    describedByIds?: string[];
    onLoaded: () => void;
}): void {
    const { store, container, input, id, signal, errorDiv, describedByIds = [], onLoaded } = opts;

    const evalState = () => {
        if (opts.visible) {
            container.style.display = opts.visible() ? "" : "none";
        }
        if (opts.disabled) {
            const isDisabled = opts.disabled();
            input.disabled = isDisabled;
            container.classList.toggle("disabled-block", isDisabled);
        }
    };

    const sync = () => {
        onLoaded();
        evalState();
    };

    store.subscribe("loaded", sync, { signal });

    // Late-mounted fields still need an initial value if the store already loaded.
    if (store.isLoaded()) {
        sync();
    }

    store.subscribe(
        "changed",
        ({ field }) => {
            evalState();
            if (field === id && document.activeElement !== input) {
                onLoaded();
            }
        },
        { signal },
    );

    setDescribedBy(input, describedByIds);

    if (errorDiv) {
        const errorId = errorDiv.id || id + "-error";
        errorDiv.id = errorId;
        errorDiv.setAttribute("aria-live", "polite");
        errorDiv.setAttribute("aria-atomic", "true");
        errorDiv.setAttribute("role", "status");
        errorDiv.style.display = "none";

        setDescribedBy(input, [errorId]);

        store.subscribe(
            "validation",
            ({ field, error }) => {
                if (field !== id) return;
                errorDiv.textContent = error ?? "";
                errorDiv.style.display = error ? "" : "none";
                input.classList.toggle("field-error-active", Boolean(error));
                if (error) {
                    input.setAttribute("aria-invalid", "true");
                } else {
                    input.removeAttribute("aria-invalid");
                }
            },
            { signal },
        );
    }
}
