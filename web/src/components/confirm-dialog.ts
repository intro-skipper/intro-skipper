import { el } from "./dom.ts";
import type { EraseOptions } from "../store/api.ts";

type CheckboxSpec = {
    label: string;
    checked?: boolean;
};

type ConfirmDialogOptions<K extends string> = {
    title: string;
    body: string;
    confirmLabel?: string;
    cancelLabel?: string;
    checkboxes?: Readonly<Record<K, CheckboxSpec>>;
};

/**
 * The erase dialogs' checkboxes, keyed by the erase option each one sets, so a
 * dialog's result can go straight into the erase call. The tombstone box is
 * checked by default, which keeps the full reset the API defaults to.
 */
export const eraseCheckboxes = {
    eraseCache: { label: "Include cached fingerprints" },
    eraseTombstones: { label: "Let deleted segments be detected again", checked: true },
} as const satisfies Record<keyof EraseOptions, CheckboxSpec>;

let dialogCounter = 0;

/** Jellyfin's own confirm prompt, as a promise. */
export function confirmDashboard(body: string, title: string): Promise<boolean> {
    return new Promise((resolve) => {
        window.Dashboard.confirm(body, title, resolve);
    });
}

/**
 * Asks for confirmation in a dialog that can carry checkboxes, where
 * window.Dashboard.confirm() is not enough. Uses the native <dialog> element
 * for focus trapping and the backdrop, and focuses Cancel so Enter does not
 * confirm.
 *
 * Checkboxes are keyed by name and shown in key order. The promise resolves
 * with each key's checked state, or null when the user cancels. A dialog
 * without checkboxes resolves to an empty record, which is still truthy.
 *
 * @example
 * const result = await confirmDialog({ title, body, checkboxes: eraseCheckboxes });
 * if (!result) return;
 * await api.eraseTimestamps(mode, result);
 */
export function confirmDialog<K extends string = never>(
    opts: ConfirmDialogOptions<K>,
): Promise<Record<K, boolean> | null> {
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

        const specs: Readonly<Record<string, CheckboxSpec>> = opts.checkboxes ?? {};
        const checkboxes = Object.entries(specs).map(([key, spec], index) => {
            const checkboxId = "is-confirm-checkbox-" + uid + "-" + String(index);
            const checkbox = el("input", { type: "checkbox", id: checkboxId });
            checkbox.checked = spec.checked ?? false;
            const label = el("label", { className: "is-confirm-checkbox-label", for: checkboxId });
            label.append(checkbox, document.createTextNode(" " + spec.label));
            const wrapper = el("div", { className: "is-confirm-checkbox-row" });
            wrapper.append(label);
            dialog.append(wrapper);
            return [key, checkbox] as const;
        });

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

        function cleanup(result: Record<K, boolean> | null): void {
            dialog.close();
            dialog.remove();
            resolve(result);
        }

        cancelBtn.addEventListener("click", () => cleanup(null));
        confirmBtn.addEventListener("click", () =>
            // The entries carry exactly the keys of opts.checkboxes; fromEntries
            // only widens them to string.
            cleanup(
                Object.fromEntries(
                    checkboxes.map(([key, checkbox]) => [key, checkbox.checked]),
                ) as Record<K, boolean>,
            ),
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
