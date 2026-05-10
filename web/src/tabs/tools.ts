import type { Tab } from "../types.ts";
import * as api from "../store/api.ts";
import { el } from "../components/dom.ts";
import { confirmDialog } from "../components/confirm-dialog.ts";
import { appendTabContent } from "../components/tab-layout.ts";
import { tabWarning } from "../components/tab-warning.ts";
import { t } from "../i18n/index.ts";

const SEGMENT_TYPES = [
    { value: "introduction", apiValue: "Introduction", labelKey: "tools_segIntroduction" },
    { value: "recap", apiValue: "Recap", labelKey: "tools_segRecap" },
    { value: "credits", apiValue: "Credits", labelKey: "tools_segCredits" },
    { value: "preview", apiValue: "Preview", labelKey: "tools_segPreview" },
    { value: "commercial", apiValue: "Commercial", labelKey: "tools_segCommercial" },
] as const;

function getSegmentType(value: string) {
    return SEGMENT_TYPES.find((segmentType) => segmentType.value === value);
}

function getSegmentLabel(value: string): string {
    const segmentType = getSegmentType(value);
    return segmentType ? t(segmentType.labelKey) : value;
}

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
        for (const segmentType of SEGMENT_TYPES) {
            globalSelect.append(
                el("option", { value: segmentType.value }, t(segmentType.labelKey)),
            );
        }
        globalSelectGroup.append(globalSelectLabel, globalSelect);

        const globalEraseBtn = el(
            "button",
            { className: "action-button raised block", type: "button" },
            t("tools_eraseAllButton", { type: getSegmentLabel("introduction") }),
        );

        globalSelect.addEventListener("change", () => {
            const label = getSegmentLabel(globalSelect.value);
            globalEraseBtn.textContent = t("tools_eraseAllButton", { type: label });
        });

        globalEraseBtn.addEventListener("click", async () => {
            const segmentType = getSegmentType(globalSelect.value);
            if (!segmentType) return;
            const typeLabel = getSegmentLabel(globalSelect.value);

            const result = await confirmDialog({
                title: t("tools_eraseDialogTitle"),
                body: t("tools_eraseDialogBody", { type: typeLabel }),
                confirmLabel: t("tools_eraseConfirmLabel"),
                checkbox: { label: t("tools_eraseIncludeFingerprints") },
            });
            if (!result) return;
            try {
                const response = await api.eraseTimestamps(segmentType.apiValue, result.checkboxChecked);
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
