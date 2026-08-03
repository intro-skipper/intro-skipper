import { validator } from "../validation/validator.ts";
import { configStore } from "../store/config-store.ts";
import * as api from "../store/api.ts";
import { el } from "./dom.ts";

/** How long the "Changes saved" message stays visible (ms). */
const STATUS_CLEAR_MS = 3000;

export function createAppShell(rootEl: HTMLElement): {
    navEl: HTMLElement;
    contentEl: HTMLElement;
    destroy: () => void;
} {
    const shell = el("div", { className: "app-shell" });

    const skipLink = el("a", { className: "skip-link", href: "#settings-main" }, "Skip to content");

    const titleId = "app-title";
    const title = el("h1", { className: "app-title", id: titleId }, "Intro Skipper Configuration");

    const header = el("header", { className: "app-header", "aria-labelledby": titleId });
    const chromaprintWarning = el("div", {
        className: "app-header-warning",
        role: "status",
        "aria-label": "Chromaprint Unavailable",
    });
    const warningIcon = el("span", {
        className: "app-header-warning-icon",
        "aria-hidden": "true",
    }, "\u26a0");
    chromaprintWarning.append(warningIcon, el("span", {}, "Chromaprint Unavailable"));
    chromaprintWarning.style.display = "none";

    header.append(title, chromaprintWarning);

    void api
        .getSupportBundle()
        .then((supportBundle) => {
            const warnings = /^\* Warnings: `([^`]*)`$/m.exec(supportBundle)?.[1] ?? "";
            const ffmpegStatus = /^\* FFmpeg: `([^`]*)`$/m.exec(supportBundle)?.[1];
            const hasIncompatibleBuildWarning = warnings
                .split(", ")
                .includes("IncompatibleFFmpegBuild");

            if (hasIncompatibleBuildWarning && ffmpegStatus === "chromaprint_not_supported") {
                chromaprintWarning.style.display = "flex";
            }
        })
        .catch(() => {
            // The header warning is advisory; the information tab still reports
            // support-bundle errors through its normal status message.
        });

    const sidebar = el("nav", { className: "app-sidebar", "aria-label": "Settings Sections" });

    const content = el("main", { className: "app-content", id: "settings-main", tabindex: "-1" });

    const footer = el("footer", { className: "app-footer", "aria-label": "Save controls" });

    const footerStatus = el("span", {
        className: "footer-status-message",
        "aria-live": "polite",
        "aria-atomic": "true",
    });
    footerStatus.style.display = "none";

    const dirtyIndicator = el(
        "span",
        { className: "dirty-indicator", "aria-live": "polite" },
        "\u25cf Unsaved changes",
    );
    dirtyIndicator.style.display = "none";

    const saveButton = el(
        "button",
        { className: "save-button", type: "button", "aria-label": "Save configuration" },
        "Save",
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
        saveButton.textContent = "Saving\u2026";
        setStatus("Saving\u2026", "info");

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

    const handleSave = () => {
        const errors = validator.validateAll(configStore.getAll());
        if (errors.size > 0) {
            // Let the user save through warnings after an explicit confirmation.
            window.Dashboard.confirm(
                "There are validation warnings. Save anyway?",
                "Validation",
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
