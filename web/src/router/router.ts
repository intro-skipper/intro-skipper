import type { Tab } from "../types.ts";
import { el } from "../components/dom.ts";
import { childScope } from "../lifecycle.ts";

/**
 * Switches between tabs inside one mounted page. Each shown tab gets a child of
 * the mount's signal; leaving the tab, or the mount ending, aborts it.
 */
export class Router {
    private tabs: Tab[] = [];
    private tabScope: AbortController | null = null;
    private readonly navEl: HTMLElement;
    private readonly contentEl: HTMLElement;
    private readonly signal: AbortSignal;

    constructor(navEl: HTMLElement, contentEl: HTMLElement, signal: AbortSignal) {
        this.navEl = navEl;
        this.contentEl = contentEl;
        this.signal = signal;
    }

    register(tab: Tab): void {
        this.tabs.push(tab);
        const button = el("button", { className: "tab-button", "data-tab-id": tab.id }, tab.label);
        button.addEventListener("click", () => this.switchTo(tab.id), { signal: this.signal });
        this.navEl.appendChild(button);
    }

    switchTo(tabId: string): void {
        this.tabScope?.abort();
        this.tabScope = null;
        this.contentEl.replaceChildren();

        const tab = this.tabs.find((t) => t.id === tabId);
        if (!tab) return;

        this.tabScope = childScope(this.signal);
        tab.render(this.contentEl, this.tabScope.signal);

        const buttons = this.navEl.querySelectorAll<HTMLButtonElement>(".tab-button");
        for (const btn of buttons) {
            btn.classList.toggle("tab-active", btn.getAttribute("data-tab-id") === tabId);
        }
    }
}
