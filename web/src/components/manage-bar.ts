import { el } from "./dom.ts";

/**
 * Appends the manage toggle button to a ts-season-bar container; the
 * stylesheet pushes it to the end of the bar. Used by seasonTabs after the
 * season tab buttons and on its own for movies.
 */
export function appendManageToggle(
    container: HTMLElement,
    opts: { managePanelId?: string; onManageToggle: (open: boolean) => void },
): void {
    let open = false;

    const btn = el("button", { className: "ts-manage-toggle", type: "button" }, "Manage");
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
