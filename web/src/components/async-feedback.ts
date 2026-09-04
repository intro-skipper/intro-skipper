import { el } from "./dom.ts";

type StatusMessageController = {
    element: HTMLElement;
    show: (text: string, color?: string) => void;
    clear: () => void;
};

export function bindStatusMessage(
    element: HTMLElement,
    opts?: { display?: "inline" | "block" },
): StatusMessageController {
    const display = opts?.display ?? "inline";

    element.setAttribute("aria-live", "polite");
    element.setAttribute("aria-atomic", "true");
    element.style.display = "none";

    return {
        element,
        show(text: string, color?: string) {
            element.textContent = text;
            element.style.color = color ?? "";
            element.style.display = text ? display : "none";
        },
        clear() {
            element.textContent = "";
            element.style.color = "";
            element.style.display = "none";
        },
    };
}

export function createStatusMessage(opts?: {
    className?: string;
    display?: "inline" | "block";
}): StatusMessageController {
    const element = el("div", { className: opts?.className ?? "status-message" });
    return bindStatusMessage(element, { display: opts?.display ?? "inline" });
}

export async function withDashboardLoading<T>(task: () => Promise<T>): Promise<T> {
    window.Dashboard.showLoadingMsg();
    try {
        return await task();
    } finally {
        window.Dashboard.hideLoadingMsg();
    }
}
