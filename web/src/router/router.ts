import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { el } from "../components/dom.ts";

export class Router {
    private tabs: Tab[] = [];
    private activeTab: Tab | null = null;
    private contentEl: HTMLElement;
    private navEl: HTMLElement;

    constructor(navEl: HTMLElement, contentEl: HTMLElement) {
        this.navEl = navEl;
        this.contentEl = contentEl;
    }

    register(tab: Tab): void {
        this.tabs.push(tab);
        const button = el("button", { className: "tab-button", "data-tab-id": tab.id }, tab.label);
        button.addEventListener("click", () => {
            this.switchTo(tab.id);
        });
        this.navEl.appendChild(button);
    }

    switchTo(tabId: string): void {
        // Remove subscriptions created by the previous tab before tearing it down.
        configStore.endScope();
        this.activeTab?.destroy?.();
        this.contentEl.replaceChildren();

        const tab = this.tabs.find((t) => t.id === tabId);
        if (!tab) return;

        // Scope new subscriptions so the next tab switch can clean them up.
        configStore.beginScope();
        tab.render(this.contentEl);
        this.activeTab = tab;

        const buttons = this.navEl.querySelectorAll<HTMLButtonElement>(".tab-button");
        for (const btn of buttons) {
            if (btn.getAttribute("data-tab-id") === tabId) {
                btn.classList.add("tab-active");
            } else {
                btn.classList.remove("tab-active");
            }
        }
    }

    destroy(): void {
        configStore.endScope();
        this.activeTab?.destroy?.();
        this.activeTab = null;
    }
}
