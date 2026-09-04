import { el } from "./dom.ts";

/**
 * Appends a spacer and manage toggle button to a ts-season-bar container.
 * Used by seasonTabs after the season tab buttons and on its own for movies.
 */
export function appendManageToggle(
    container: HTMLElement,
    opts: { managePanelId?: string; onManageToggle: (open: boolean) => void },
): void {
    let open = false;

    container.append(el("div", { className: "ts-season-spacer" }));

    const btn = el(
        "button",
        { className: "ts-manage-toggle", type: "button" },
        "⚙ Manage",
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
