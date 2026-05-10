import type { Tab } from "../types.ts";
import { createTimestampsBrowser } from "./timestamps-browser.ts";
import { t } from "../i18n/index.ts";

let activeBrowser: ReturnType<typeof createTimestampsBrowser> | null = null;

export const timestampsTab: Tab = {
    id: "timestamps",
    getLabel: () => t("tab_timestamps"),

    render(container) {
        activeBrowser?.destroy();
        activeBrowser = createTimestampsBrowser(container);
    },

    destroy() {
        activeBrowser?.destroy();
        activeBrowser = null;
    },
};
