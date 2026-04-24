import { configStore } from "../store/config-store.ts";

/** Field options used by the shared binding helper. */
export type BindFieldOpts = {
    id: string;
    disabled?: () => boolean;
    visible?: () => boolean;
};

function setDescribedBy(
    input: HTMLInputElement | HTMLSelectElement,
    ids: string[] | undefined,
): void {
    if (!ids || ids.length === 0) {
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
 * Shared wiring for form field components.
 * Handles visibility, disabled state, and validation subscriptions
 * that are identical across checkbox, number, text, and select fields.
 */
export function bindField(opts: {
    container: HTMLElement;
    input: HTMLInputElement | HTMLSelectElement;
    fieldOpts: BindFieldOpts;
    errorDiv?: HTMLElement;
    describedByIds?: string[];
    onLoaded: () => void;
}): void {
    const { container, input, fieldOpts, errorDiv, describedByIds, onLoaded } = opts;

    const evalVisibility = () => {
        if (fieldOpts.visible) {
            container.style.display = fieldOpts.visible() ? "" : "none";
        }
    };

    const evalDisabled = () => {
        if (fieldOpts.disabled) {
            const isDisabled = fieldOpts.disabled();
            input.disabled = isDisabled;
            container.classList.toggle("disabled-block", isDisabled);
        }
    };

    configStore.subscribe("loaded", () => {
        onLoaded();
        evalVisibility();
        evalDisabled();
    });

    // Late-mounted fields still need an initial value if the config already loaded.
    if (configStore.isLoaded()) {
        onLoaded();
        evalVisibility();
        evalDisabled();
    }

    if (fieldOpts.visible) {
        configStore.subscribe("changed", evalVisibility);
    }

    if (fieldOpts.disabled) {
        configStore.subscribe("changed", evalDisabled);
    }

    configStore.subscribe("changed", (...args: unknown[]) => {
        const data = args[0] as { field?: string } | undefined;
        if (data?.field !== fieldOpts.id) return;
        if (document.activeElement === input) return;
        onLoaded();
    });

    setDescribedBy(input, describedByIds);

    if (errorDiv) {
        const errorId = errorDiv.id || fieldOpts.id + "-error";
        errorDiv.id = errorId;
        errorDiv.setAttribute("aria-live", "polite");
        errorDiv.setAttribute("aria-atomic", "true");
        errorDiv.setAttribute("role", "status");

        setDescribedBy(input, [errorId]);

        configStore.subscribe("validation", (...args: unknown[]) => {
            const data = args[0] as { field: string; error: string | null };
            if (data.field !== fieldOpts.id) return;
            if (data.error) {
                errorDiv.textContent = data.error;
                errorDiv.style.display = "";
                input.classList.add("field-error-active");
                input.setAttribute("aria-invalid", "true");
            } else {
                errorDiv.textContent = "";
                errorDiv.style.display = "none";
                input.classList.remove("field-error-active");
                input.removeAttribute("aria-invalid");
            }
        });
    }
}
