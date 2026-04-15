import { el } from "./dom.ts";

export type StatusMessageController = {
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

export async function loadTextContent(opts: {
  load: () => Promise<string>;
  textarea: HTMLTextAreaElement;
  status: StatusMessageController;
  loadingText: string;
  loadedText: string;
  emptyText: string;
  errorText: string;
  onLoaded?: (text: string) => void;
  onError?: () => void;
}): Promise<void> {
  opts.status.show(opts.loadingText);

  try {
    const text = await opts.load();
    opts.textarea.value = text;
    opts.status.show(text ? opts.loadedText : opts.emptyText);
    opts.onLoaded?.(text);
  } catch {
    opts.textarea.value = "";
    opts.status.show(opts.errorText, "var(--is-error)");
    opts.onError?.();
  }
}
