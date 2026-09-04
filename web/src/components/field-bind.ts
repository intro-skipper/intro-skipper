import type { PluginConfig } from "../types.ts";
import { configStore } from "../store/config-store.ts";

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
 * Subscribes to config store events to toggle `container` visibility.
 * Must be called within a `configStore.beginScope()` / `endScope()` window
 * so the subscriptions are tracked and cleaned up on scope disposal.
 */
export function bindVisibility(container: HTMLElement, visible?: () => boolean): void {
    if (!visible) {
        return;
    }

    const evalVisibility = () => {
        container.style.display = visible() ? "" : "none";
    };

    configStore.subscribe("loaded", evalVisibility);
    configStore.subscribe("changed", evalVisibility);

    if (configStore.isLoaded()) {
        evalVisibility();
    }
}

/**
 * Shared wiring for config-bound controls: initial value, visibility, disabled
 * state, and validation messages. `onLoaded` copies the store value into the
 * control; it is skipped while the control has focus so typing is not clobbered.
 */
export function bindField(opts: {
    container: HTMLElement;
    input: HTMLInputElement | HTMLSelectElement;
    fieldOpts: { id: keyof PluginConfig; disabled?: () => boolean; visible?: () => boolean };
    errorDiv?: HTMLElement;
    describedByIds?: string[];
    onLoaded: () => void;
}): void {
    const { container, input, fieldOpts, errorDiv, describedByIds = [], onLoaded } = opts;

    const evalState = () => {
        if (fieldOpts.visible) {
            container.style.display = fieldOpts.visible() ? "" : "none";
        }
        if (fieldOpts.disabled) {
            const isDisabled = fieldOpts.disabled();
            input.disabled = isDisabled;
            container.classList.toggle("disabled-block", isDisabled);
        }
    };

    const sync = () => {
        onLoaded();
        evalState();
    };

    configStore.subscribe("loaded", sync);

    // Late-mounted fields still need an initial value if the config already loaded.
    if (configStore.isLoaded()) {
        sync();
    }

    configStore.subscribe("changed", ({ field }) => {
        evalState();
        if (field === fieldOpts.id && document.activeElement !== input) {
            onLoaded();
        }
    });

    setDescribedBy(input, describedByIds);

    if (errorDiv) {
        const errorId = errorDiv.id || fieldOpts.id + "-error";
        errorDiv.id = errorId;
        errorDiv.setAttribute("aria-live", "polite");
        errorDiv.setAttribute("aria-atomic", "true");
        errorDiv.setAttribute("role", "status");
        errorDiv.style.display = "none";

        setDescribedBy(input, [errorId]);

        configStore.subscribe("validation", ({ field, error }) => {
            if (field !== fieldOpts.id) return;
            errorDiv.textContent = error ?? "";
            errorDiv.style.display = error ? "" : "none";
            input.classList.toggle("field-error-active", Boolean(error));
            if (error) {
                input.setAttribute("aria-invalid", "true");
            } else {
                input.removeAttribute("aria-invalid");
            }
        });
    }
}
