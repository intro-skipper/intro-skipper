import { el } from "./dom.ts";

export type ManageBarOptions = {
    managePanelId?: string;
    onManageToggle: (open: boolean) => void;
};

/**
 * Creates a standalone ts-season-bar containing a spacer and manage toggle button.
 * Used for movies and other single-item contexts where no season tabs are present.
 */
export function createManageBar(opts: ManageBarOptions): { container: HTMLElement } {
    const container = el("div", { className: "ts-season-bar" });
    appendManageToggle(container, opts);
    return { container };
}

/**
 * Appends a spacer and manage toggle button to an existing container.
 * Used by seasonTabs to add the manage button after the season tab buttons.
 */
export function appendManageToggle(container: HTMLElement, opts: ManageBarOptions): void {
    let open = false;

    container.append(el("div", { className: "ts-season-spacer" }));

    const btn = el(
        "button",
        { className: "ts-manage-toggle", type: "button" },
        "\u2699 Manage",
    ) as HTMLButtonElement;
    btn.setAttribute("aria-label", "Toggle management panel");
    btn.setAttribute("aria-expanded", "false");
    if (opts.managePanelId) {
        btn.setAttribute("aria-controls", opts.managePanelId);
    }
    btn.addEventListener("click", () => {
        open = !open;
        btn.setAttribute("aria-expanded", String(open));
        opts.onManageToggle(open);
    });
    container.append(btn);
}
