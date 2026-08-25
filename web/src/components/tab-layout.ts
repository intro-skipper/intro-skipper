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
