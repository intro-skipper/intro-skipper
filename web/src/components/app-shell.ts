import { validateAll } from "../config/validate.ts";
import { configStore } from "../store/config-store.ts";
import { bindStatusMessage, type StatusState } from "./async-feedback.ts";
import { confirmDashboard } from "./confirm-dialog.ts";
import { el } from "./dom.ts";

/** How long the "Changes saved" message stays visible (ms). */
const STATUS_CLEAR_MS = 3000;

/**
 * Header, tab sidebar, content area and the save footer. Everything it wires
 * up ends when `signal` aborts.
 */
export function createAppShell(
    rootEl: HTMLElement,
    signal: AbortSignal,
): {
    navEl: HTMLElement;
    contentEl: HTMLElement;
} {
    const shell = el("div", { className: "is-app-shell" });

    const skipLink = el("a", { className: "skip-link", href: "#settings-main" }, "Skip to content");

    const titleId = "app-title";
    const title = el("h1", { className: "app-title", id: titleId }, "Intro Skipper Configuration");

    const header = el("header", { className: "app-header", "aria-labelledby": titleId });
    header.append(title);

    const sidebar = el("nav", { className: "app-sidebar", "aria-label": "Settings Sections" });

    const content = el("main", { className: "app-content", id: "settings-main", tabindex: "-1" });

    const footer = el("footer", { className: "app-footer", "aria-label": "Save controls" });

    const footerStatus = el("span", { className: "footer-status-message" });
    const status = bindStatusMessage(footerStatus);

    const dirtyIndicator = el(
        "span",
        { className: "dirty-indicator", "aria-live": "polite" },
        "Unsaved changes",
    );

    const saveButton = el(
        "button",
        { className: "save-button", type: "button", "aria-label": "Save configuration" },
        "Save",
    );

    footer.append(footerStatus, dirtyIndicator, saveButton);
    shell.append(skipLink, header, sidebar, content, footer);
    rootEl.append(shell);

    let statusTimer: number | null = null;

    const setStatus = (text: string, state?: StatusState) => {
        if (statusTimer !== null) {
            window.clearTimeout(statusTimer);
            statusTimer = null;
        }

        status.show(text, state);

        if (state === "success") {
            statusTimer = window.setTimeout(() => {
                if (!configStore.isDirty()) {
                    status.clear();
                }
            }, STATUS_CLEAR_MS);
        }
    };

    const runSave = async () => {
        if (saveButton.disabled) return;

        saveButton.disabled = true;
        saveButton.textContent = "Saving…";
        setStatus("Saving…");

        try {
            await configStore.save();
            setStatus("Changes saved", "success");
        } catch {
            setStatus("Save failed", "error");
            window.Dashboard.alert("Failed to save configuration");
        } finally {
            saveButton.disabled = false;
            saveButton.textContent = "Save";
        }
    };

    const handleSave = async () => {
        const errors = validateAll(configStore.getAll());
        // Let the user save through warnings after an explicit confirmation.
        if (
            errors.size === 0 ||
            (await confirmDashboard("There are validation warnings. Save anyway?", "Validation"))
        ) {
            await runSave();
        }
    };

    skipLink.addEventListener(
        "click",
        (event) => {
            event.preventDefault();
            content.focus();
        },
        { signal },
    );
    saveButton.addEventListener(
        "click",
        () => {
            handleSave().catch(console.error);
        },
        { signal },
    );
    window.addEventListener(
        "beforeunload",
        (event) => {
            if (!configStore.isDirty()) return;
            event.preventDefault();
            event.returnValue = "";
        },
        { signal },
    );

    const updateDirtyIndicator = () => {
        const isDirty = configStore.isDirty();
        dirtyIndicator.toggleAttribute("data-dirty", isDirty);
        if (isDirty && footerStatus.dataset.state !== "error") {
            status.clear();
        }
    };

    const clearDirtyIndicator = () => {
        dirtyIndicator.removeAttribute("data-dirty");
    };

    // Keep the unsaved indicator aligned with the store lifecycle.
    configStore.subscribe("changed", updateDirtyIndicator, { signal });
    configStore.subscribe("saved", clearDirtyIndicator, { signal });
    configStore.subscribe("loaded", clearDirtyIndicator, { signal });

    signal.addEventListener(
        "abort",
        () => {
            if (statusTimer !== null) window.clearTimeout(statusTimer);
        },
        { once: true },
    );

    return { navEl: sidebar, contentEl: content };
}
