import type { Tab } from "../types.ts";
import { createTimestampsBrowser } from "./timestamps-browser.ts";

let activeBrowser: ReturnType<typeof createTimestampsBrowser> | null = null;

export const timestampsTab: Tab = {
    id: "timestamps",
    label: "Timestamps",

    render(container) {
        activeBrowser?.destroy();
        activeBrowser = createTimestampsBrowser(container);
    },

    destroy() {
        activeBrowser?.destroy();
        activeBrowser = null;
    },
};
