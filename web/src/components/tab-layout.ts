import { el } from "./dom.ts";

type TabContentItem = Node | null | undefined | false;

export function appendTabContent(container: HTMLElement, ...items: TabContentItem[]): void {
  const nodes = items.filter((item): item is Node => item instanceof Node);
  if (nodes.length > 0) {
    container.append(...nodes);
  }
}

export function fieldRow(...children: HTMLElement[]): HTMLElement {
  const row = el("div", { className: "side-by-side" });
  row.append(...children);
  return row;
}

export function readonlyTextSection(opts: {
  title: string;
  labelId: string;
  statusId: string;
  description?: string;
  rows?: number;
  cols?: number;
}): {
  container: HTMLElement;
  titleEl: HTMLElement;
  statusEl: HTMLElement;
  textareaEl: HTMLTextAreaElement;
} {
  const container = el("section", { className: "tab-readonly-section" });

  const titleEl = el("h3", { className: "checkbox-list-label" }, opts.title);
  titleEl.id = opts.labelId;

  const statusEl = el("div", { className: "status-message", id: opts.statusId });
  statusEl.setAttribute("aria-live", "polite");
  statusEl.setAttribute("aria-atomic", "true");

  const textareaEl = el("textarea", {
    rows: String(opts.rows ?? 20),
    cols: String(opts.cols ?? 75),
    readonly: "",
  }) as HTMLTextAreaElement;
  textareaEl.setAttribute("aria-labelledby", titleEl.id);
  textareaEl.setAttribute("aria-describedby", statusEl.id);

  appendTabContent(
    container,
    titleEl,
    opts.description ? el("div", { className: "field-description" }, opts.description) : null,
    statusEl,
    textareaEl,
  );

  return { container, titleEl, statusEl, textareaEl };
}
