import { el } from "./dom.ts";

/**
 * A custom confirmation dialog that supports an optional checkbox,
 * replacing window.Dashboard.confirm() where richer content is needed.
 *
 * Uses the native <dialog> element for built-in focus trapping and
 * backdrop handling. Returns a promise that resolves with the user's
 * choices or null if cancelled.
 */

type ConfirmDialogOptions = {
    title: string;
    body: string;
    confirmLabel?: string;
    cancelLabel?: string;
    checkbox?: {
        label: string;
    };
};

type ConfirmDialogResult = {
    checkboxChecked: boolean;
};

let dialogCounter = 0;

/** Jellyfin's own confirm prompt, as a promise. */
export function confirmDashboard(body: string, title: string): Promise<boolean> {
    return new Promise((resolve) => {
        window.Dashboard.confirm(body, title, resolve);
    });
}

export function confirmDialog(opts: ConfirmDialogOptions): Promise<ConfirmDialogResult | null> {
    return new Promise((resolve) => {
        const uid = String(++dialogCounter);
        const titleId = "is-confirm-title-" + uid;
        const bodyId = "is-confirm-body-" + uid;

        const dialog = el("dialog", { className: "is-confirm-dialog" });
        dialog.setAttribute("aria-labelledby", titleId);
        dialog.setAttribute("aria-describedby", bodyId);

        const heading = el("h2", { id: titleId, className: "is-confirm-title" }, opts.title);
        const body = el("p", { id: bodyId, className: "is-confirm-body" }, opts.body);

        dialog.append(heading, body);

        let checkbox: HTMLInputElement | null = null;

        if (opts.checkbox) {
            const checkboxId = "is-confirm-checkbox-" + uid;
            checkbox = el("input", { type: "checkbox", id: checkboxId }) as HTMLInputElement;
            const label = el("label", { className: "is-confirm-checkbox-label", for: checkboxId });
            label.append(checkbox, document.createTextNode(" " + opts.checkbox.label));
            const wrapper = el("div", { className: "is-confirm-checkbox-row" });
            wrapper.append(label);
            dialog.append(wrapper);
        }

        const cancelBtn = el(
            "button",
            { className: "is-confirm-btn cancel", type: "button" },
            opts.cancelLabel ?? "Cancel",
        );
        const confirmBtn = el(
            "button",
            { className: "is-confirm-btn confirm", type: "button" },
            opts.confirmLabel ?? "Confirm",
        );

        const actions = el("div", { className: "is-confirm-actions" });
        actions.append(cancelBtn, confirmBtn);
        dialog.append(actions);

        function cleanup(result: ConfirmDialogResult | null): void {
            dialog.close();
            dialog.remove();
            resolve(result);
        }

        cancelBtn.addEventListener("click", () => cleanup(null));
        confirmBtn.addEventListener("click", () =>
            cleanup({ checkboxChecked: checkbox?.checked ?? false }),
        );

        // Esc key triggers the cancel event on <dialog>.
        dialog.addEventListener("cancel", (e) => {
            e.preventDefault();
            cleanup(null);
        });

        // Close when clicking the backdrop (the ::backdrop pseudo-element
        // doesn't receive events, but clicks on the dialog element itself
        // outside the content box do).
        dialog.addEventListener("click", (e) => {
            if (e.target === dialog) cleanup(null);
        });

        document.body.append(dialog);
        dialog.showModal();
        // Focus the cancel button by default so the destructive action
        // requires deliberate intent (keyboard Enter won't confirm).
        cancelBtn.focus();
    });
}
