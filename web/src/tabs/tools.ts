import type { Tab } from "../types.ts";
import * as api from "../store/api.ts";
import { el } from "../components/dom.ts";
import { confirmDialog } from "../components/confirm-dialog.ts";
import { appendTabContent } from "../components/tab-layout.ts";
import { tabWarning } from "../components/tab-warning.ts";
import { t } from "../i18n/index.ts";

export const toolsTab: Tab = {
    id: "tools",
    label: t("tab_tools"),

    render(container) {
        const globalSelectId = "global-timestamp-type";
        const globalSelectGroup = el("div", { className: "select-container" });
        const globalSelectLabel = el(
            "label",
            { className: "select-label", for: globalSelectId },
            t("tools_globalTimestampTypeLabel"),
        );
        const globalSelect = el("select", {
            id: globalSelectId,
            name: "global-timestamp-type",
        });
        globalSelect.append(el("option", { value: "introduction" }, t("tools_segIntroduction")));
        globalSelect.append(el("option", { value: "recap" }, t("tools_segRecap")));
        globalSelect.append(el("option", { value: "credits" }, t("tools_segCredits")));
        globalSelect.append(el("option", { value: "preview" }, t("tools_segPreview")));
        globalSelect.append(el("option", { value: "commercial" }, t("tools_segCommercial")));
        globalSelectGroup.append(globalSelectLabel, globalSelect);

        const globalEraseBtn = el(
            "button",
            { className: "action-button raised block", type: "button" },
            t("tools_eraseAllButton", { type: t("tools_segIntroduction") }),
        );

        globalSelect.addEventListener("change", () => {
            const typeMap: Record<string, string> = {
                introduction: t("tools_segIntroduction"),
                recap: t("tools_segRecap"),
                credits: t("tools_segCredits"),
                preview: t("tools_segPreview"),
                commercial: t("tools_segCommercial"),
            };
            const label = typeMap[globalSelect.value] ?? globalSelect.value;
            globalEraseBtn.textContent = t("tools_eraseAllButton", { type: label });
        });

        globalEraseBtn.addEventListener("click", async () => {
            const typeMap: Record<string, string> = {
                introduction: "Introduction",
                recap: "Recap",
                credits: "Credits",
                preview: "Preview",
                commercial: "Commercial",
            };
            const type = typeMap[globalSelect.value];
            if (!type) return;

            const typeLabel = (() => {
                const labelMap: Record<string, string> = {
                    introduction: t("tools_segIntroduction"),
                    recap: t("tools_segRecap"),
                    credits: t("tools_segCredits"),
                    preview: t("tools_segPreview"),
                    commercial: t("tools_segCommercial"),
                };
                return labelMap[globalSelect.value] ?? globalSelect.value;
            })();

            const result = await confirmDialog({
                title: t("tools_eraseDialogTitle"),
                body: t("tools_eraseDialogBody", { type: typeLabel }),
                confirmLabel: t("tools_eraseConfirmLabel"),
                checkbox: { label: t("tools_eraseIncludeFingerprints") },
            });
            if (!result) return;
            try {
                const response = await api.eraseTimestamps(type, result.checkboxChecked);
                if (!response.ok) {
                    window.Dashboard.alert(t("tools_eraseFailed", { type: typeLabel }));
                    return;
                }
                window.Dashboard.alert(t("tools_eraseSuccess", { type: typeLabel }));
            } catch {
                window.Dashboard.alert(t("tools_eraseFailed", { type: typeLabel }));
            }
        });

        const rebuildBtn = el(
            "button",
            { className: "action-button raised block", type: "button" },
            t("tools_rebuildButton"),
        );
        rebuildBtn.addEventListener("click", async () => {
            const result = await confirmDialog({
                title: t("tools_rebuildDialogTitle"),
                body: t("tools_rebuildDialogBody"),
                confirmLabel: t("tools_rebuildConfirmLabel"),
            });
            if (!result) return;
            try {
                const response = await api.rebuildDatabase();
                if (!response.ok) {
                    window.Dashboard.alert(t("tools_rebuildFailed"));
                    return;
                }
                window.Dashboard.alert(t("tools_rebuildSuccess"));
            } catch {
                window.Dashboard.alert(t("tools_rebuildFailed"));
            }
        });

        appendTabContent(
            container,
            globalSelectGroup,
            globalEraseBtn,
            rebuildBtn,
            tabWarning(t("tools_rebuildWarning")),
        );
    },
};
