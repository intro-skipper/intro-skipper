import "./styles/variables.css";
import "./styles/layout.css";
import "./styles/forms.css";

import { createAppShell } from "./components/app-shell.ts";
import { Router } from "./router/router.ts";
import { configStore } from "./store/config-store.ts";

import type { Tab } from "./types.ts";
import { generalTab } from "./tabs/general.ts";
import { analysisTab } from "./tabs/analysis.ts";
import { detectionTab } from "./tabs/detection.ts";
import { blackFrameTab } from "./tabs/black-frame.ts";
import { chaptersTab } from "./tabs/chapters.ts";
import { ffmpegTab } from "./tabs/ffmpeg.ts";
import { timestampsTab } from "./tabs/timestamps-browser.ts";
import { toolsTab } from "./tabs/tools.ts";
import { informationTab } from "./tabs/information.ts";

const tabs: readonly Tab[] = [
    generalTab,
    analysisTab,
    detectionTab,
    blackFrameTab,
    chaptersTab,
    ffmpegTab,
    timestampsTab,
    toolsTab,
    informationTab,
];

const ROOT_SELECTOR = "#intro-skipper-dashboard-root";
const DEFAULT_TAB_ID = "general";

let cleanupPage: (() => void) | null = null;
let mountVersion = 0;
let boundRoot: HTMLElement | null = null;

function destroyMountedPage(): void {
    mountVersion += 1;
    cleanupPage?.();
    cleanupPage = null;
}

function getPageElement(root: HTMLElement): HTMLElement {
    const page = root.closest<HTMLElement>(".page");
    if (!page) {
        console.warn(
            "[intro-skipper] No .page ancestor found; pageshow/pagehide lifecycle events will not fire.",
        );
    }
    return page ?? root;
}

function mountPage(rootEl: HTMLElement): void {
    destroyMountedPage();

    rootEl.replaceChildren();

    const currentMountVersion = mountVersion;
    const { navEl, contentEl, destroy: destroyShell } = createAppShell(rootEl);
    const router = new Router(navEl, contentEl);

    cleanupPage = () => {
        router.destroy();
        destroyShell();
    };

    for (const tab of tabs) {
        router.register(tab);
    }

    void configStore
        .load()
        .then(() => {
            if (currentMountVersion !== mountVersion) {
                return;
            }

            router.switchTo(DEFAULT_TAB_ID);
        })
        .catch(() => {
            /* already logged & alerted in configStore.load() */
        });
}

function bindPage(rootEl: HTMLElement): void {
    if (rootEl.dataset.introSkipperBound === "true") {
        return;
    }

    rootEl.dataset.introSkipperBound = "true";
    boundRoot = rootEl;

    const page = getPageElement(rootEl);

    const handlePageShow = () => {
        mountPage(rootEl);
    };

    const handlePageHide = () => {
        destroyMountedPage();
    };

    page.addEventListener("pageshow", handlePageShow);
    page.addEventListener("pagehide", handlePageHide);

    mountPage(rootEl);
}

function findAndBind(): boolean {
    const rootEl = document.querySelector<HTMLElement>(ROOT_SELECTOR);
    if (!rootEl) {
        return false;
    }

    bindPage(rootEl);
    return true;
}

findAndBind();

// This bundle is loaded as `<script type="module">`, so the browser's module
// map evaluates it at most once per document. Jellyfin's view manager keeps
// only a few views cached and re-injects the config page HTML (including this
// script tag) with a brand-new DOM once the view has been evicted, but that
// re-injection never re-runs an already-evaluated module. Keep observing for
// the lifetime of the document so a freshly injected, unbound root is picked
// up and bound again.
const observer = new MutationObserver(() => {
    if (boundRoot) {
        if (boundRoot.isConnected) {
            return;
        }

        // Release the evicted view subtree so it can be garbage collected.
        boundRoot = null;
    }

    findAndBind();
});

const observerRoot = document.body ?? document.documentElement;
observer.observe(observerRoot, { childList: true, subtree: true });
