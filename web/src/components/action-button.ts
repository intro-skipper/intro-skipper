import { el } from "./dom.ts";

/** A text button that stays disabled while `onClick` runs. `danger` colors it as destructive. */
export function actionButton(
    label: string,
    onClick: () => Promise<void> | void,
    { danger = false } = {},
): HTMLElement {
    const button = el(
        "button",
        { className: danger ? "action-button danger" : "action-button", type: "button" },
        label,
    );

    button.addEventListener("click", async () => {
        button.disabled = true;
        try {
            await onClick();
        } finally {
            button.disabled = false;
        }
    });

    return button;
}
