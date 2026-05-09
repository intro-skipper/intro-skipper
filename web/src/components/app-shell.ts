import { validator } from "../validation/validator.ts";
import { configStore } from "../store/config-store.ts";
import { el } from "./dom.ts";
import { t } from "../i18n/index.ts";

/** How long the "Changes saved" message stays visible (ms). */
const STATUS_CLEAR_MS = 3000;

export function createAppShell(rootEl: HTMLElement): {
    navEl: HTMLElement;
    contentEl: HTMLElement;
    destroy: () => void;
} {
    const shell = el("div", { className: "app-shell" });

    const skipLink = el("a", { className: "skip-link", href: "#settings-main" }, t("shell_skipToContent"));

    const titleId = "app-title";
    const title = el("h1", { className: "app-title", id: titleId }, t("shell_title"));

    const header = el("header", { className: "app-header", "aria-labelledby": titleId });
    header.append(title);

    const sidebar = el("nav", { className: "app-sidebar", "aria-label": t("shell_settingsSections") });

    const content = el("main", { className: "app-content", id: "settings-main", tabindex: "-1" });

    const footer = el("footer", { className: "app-footer", "aria-label": t("shell_saveControls") });

    const footerStatus = el("span", {
        className: "footer-status-message",
        "aria-live": "polite",
        "aria-atomic": "true",
    });
    footerStatus.style.display = "none";

    const dirtyIndicator = el(
        "span",
        { className: "dirty-indicator", "aria-live": "polite" },
        t("shell_unsavedChanges"),
    );
    dirtyIndicator.style.display = "none";

    const saveButton = el(
        "button",
        { className: "save-button", type: "button", "aria-label": t("shell_saveAriaLabel") },
        t("shell_save"),
    );

    footer.append(footerStatus, dirtyIndicator, saveButton);
    shell.append(skipLink, header, sidebar, content, footer);
    rootEl.append(shell);

    let statusTimer: number | null = null;

    const clearStatus = () => {
        footerStatus.textContent = "";
        footerStatus.dataset.state = "";
        footerStatus.style.display = "none";
    };

    const setStatus = (text: string, state: "info" | "success" | "error") => {
        if (statusTimer !== null) {
            window.clearTimeout(statusTimer);
            statusTimer = null;
        }

        footerStatus.textContent = text;
        footerStatus.dataset.state = state;
        footerStatus.style.display = text ? "inline" : "none";

        if (state === "success") {
            statusTimer = window.setTimeout(() => {
                if (!configStore.isDirty()) {
                    clearStatus();
                }
            }, STATUS_CLEAR_MS);
        }
    };

    const handleSkipLink = (event: MouseEvent) => {
        event.preventDefault();
        content.focus();
    };

    const runSave = async () => {
        if (saveButton.disabled) return;

        saveButton.disabled = true;
        saveButton.textContent = t("shell_saving");
        setStatus(t("shell_saving"), "info");

        try {
            await configStore.save();
            setStatus(t("shell_changesSaved"), "success");
        } catch {
            setStatus(t("shell_saveFailed"), "error");
            window.Dashboard.alert(t("shell_failedToSaveConfig"));
        } finally {
            saveButton.disabled = false;
            saveButton.textContent = t("shell_save");
        }
    };

    const handleSave = () => {
        const errors = validator.validateAll(configStore.getAll());
        if (errors.size > 0) {
            // Let the user save through warnings after an explicit confirmation.
            window.Dashboard.confirm(
                t("shell_validationWarning"),
                t("shell_validationTitle"),
                (result: boolean) => {
                    if (result) {
                        void runSave();
                    }
                },
            );
        } else {
            void runSave();
        }
    };

    const handleBeforeUnload = (event: BeforeUnloadEvent) => {
        if (!configStore.isDirty()) return;
        event.preventDefault();
        event.returnValue = "";
    };

    skipLink.addEventListener("click", handleSkipLink);
    saveButton.addEventListener("click", handleSave);
    window.addEventListener("beforeunload", handleBeforeUnload);

    const updateDirtyIndicator = () => {
        const isDirty = configStore.isDirty();
        dirtyIndicator.style.display = isDirty ? "inline" : "none";
        if (isDirty && footerStatus.dataset.state !== "error") {
            clearStatus();
        }
    };

    const clearDirtyIndicator = () => {
        dirtyIndicator.style.display = "none";
    };

    // Keep the unsaved indicator aligned with the store lifecycle.
    configStore.subscribe("changed", updateDirtyIndicator);
    configStore.subscribe("saved", clearDirtyIndicator);
    configStore.subscribe("loaded", clearDirtyIndicator);

    return {
        navEl: sidebar,
        contentEl: content,
        destroy() {
            skipLink.removeEventListener("click", handleSkipLink);
            saveButton.removeEventListener("click", handleSave);
            window.removeEventListener("beforeunload", handleBeforeUnload);
            configStore.unsubscribe("changed", updateDirtyIndicator);
            configStore.unsubscribe("saved", clearDirtyIndicator);
            configStore.unsubscribe("loaded", clearDirtyIndicator);
            if (statusTimer !== null) {
                window.clearTimeout(statusTimer);
            }
        },
    };
}
