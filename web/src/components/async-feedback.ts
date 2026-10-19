import { el } from "./dom.ts";

/** How a status line reads: plain progress text, or colored as a success, an error or a warning. */
export type StatusState = "success" | "error" | "warning";

type StatusMessageController = {
    element: HTMLElement;
    show: (text: string, state?: StatusState) => void;
    clear: () => void;
};

/**
 * Turns `element` into a live status line. The stylesheet colors it by its
 * data-state and hides it while it is empty.
 */
export function bindStatusMessage(element: HTMLElement): StatusMessageController {
    element.setAttribute("aria-live", "polite");
    element.setAttribute("aria-atomic", "true");

    return {
        element,
        show(text, state) {
            element.textContent = text;
            if (state) element.dataset.state = state;
            else delete element.dataset.state;
        },
        clear() {
            element.textContent = "";
            delete element.dataset.state;
        },
    };
}

export function createStatusMessage(className = "status-message"): StatusMessageController {
    return bindStatusMessage(el("div", { className }));
}

export async function withDashboardLoading<T>(task: () => Promise<T>): Promise<T> {
    window.Dashboard.showLoadingMsg();
    try {
        return await task();
    } finally {
        window.Dashboard.hideLoadingMsg();
    }
}
