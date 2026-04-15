import type { Tab } from "../types.ts";
import * as api from "../store/api.ts";
import { el } from "../components/dom.ts";
import { bindStatusMessage, loadTextContent } from "../components/async-feedback.ts";
import { appendTabContent, readonlyTextSection } from "../components/tab-layout.ts";

export const informationTab: Tab = {
  id: "information",
  label: "Information",
  render(container) {
    const supportSection = readonlyTextSection({
      title: "Intro Skipper Support Log",
      labelId: "support-log-label",
      statusId: "support-log-status",
    });
    const supportStatus = bindStatusMessage(supportSection.statusEl, { display: "block" });
    supportStatus.show("Loading support log\u2026");
    const supportTextarea = supportSection.textareaEl;

    const copyButtonEl = el(
      "button",
      {
        className: "raised button-submit",
        type: "button",
      },
      "Copy to Clipboard",
    ) as HTMLButtonElement;
    copyButtonEl.disabled = true;
    copyButtonEl.setAttribute("aria-describedby", supportStatus.element.id);
    copyButtonEl.addEventListener("click", async () => {
      const text = supportTextarea.value;
      if (!text) return;
      try {
        await navigator.clipboard.writeText(text);
        window.Dashboard.alert("Support bundle copied to clipboard");
      } catch {
        supportTextarea.focus();
        supportTextarea.setSelectionRange(0, text.length);
        window.Dashboard.alert("Press Ctrl+C to copy support bundle");
      }
    });
    supportSection.container.append(copyButtonEl);

    async function loadSupportBundle(): Promise<void> {
      await loadTextContent({
        load: () => api.getSupportBundle(),
        textarea: supportTextarea,
        status: supportStatus,
        loadingText: "Loading support log\u2026",
        loadedText: "Support log loaded.",
        emptyText: "Support log is empty.",
        errorText: "Failed to load support log.",
        onLoaded: (text) => {
          copyButtonEl.disabled = !text;
        },
        onError: () => {
          copyButtonEl.disabled = true;
        },
      });
    }

    loadSupportBundle().catch(console.error);

    // Storage usage — structured list with progress bars.
    const storageContainer = el("section", { className: "tab-readonly-section" });
    const storageTitle = el("h3", { className: "checkbox-list-label" }, "Storage Usage");
    storageTitle.id = "storage-usage-label";
    const storageDesc = el(
      "div",
      { className: "field-description" },
      "See how much space each library uses.",
    );
    const storageStatusEl = el("div", { className: "status-message", id: "storage-usage-status" });
    const storageStatus = bindStatusMessage(storageStatusEl, { display: "block" });
    storageStatus.show("Loading storage usage\u2026");
    const storageList = el("div", {});
    storageList.setAttribute("aria-labelledby", storageTitle.id);
    appendTabContent(storageContainer, storageTitle, storageDesc, storageStatusEl, storageList);

    function formatSize(bytes: number): string {
      if (bytes <= 0) return "0 B";
      const units = ["B", "KB", "MB", "GB", "TB"];
      const i = Math.floor(Math.log(bytes) / Math.log(1024));
      return (bytes / Math.pow(1024, i)).toFixed(1) + " " + units[i];
    }

    function barColor(pct: number): string {
      if (pct >= 90) return "var(--is-error)";
      if (pct >= 75) return "var(--is-warning)";
      return "var(--is-success)";
    }

    function buildListItem(name: string, path: string, used: number, free: number): HTMLElement {
      const total = used + free;
      const pct = total > 0 ? (used / total) * 100 : 0;

      const item = el("li", { className: "storage-item" });

      const text = el("div", { className: "storage-item-body" });
      text.append(el("div", { className: "storage-item-name" }, name));
      text.append(el("div", { className: "storage-item-path" }, path));

      const track = el("div", { className: "storage-bar-track" });
      const fill = el("div", {
        className: "storage-bar-fill",
        style: `width:${pct.toFixed(1)}%;background:${barColor(pct)};`,
      });
      track.append(fill);
      text.append(track);

      text.append(
        el(
          "div",
          { className: "storage-item-usage" },
          `${formatSize(used)} / ${formatSize(total)}`,
        ),
      );

      item.append(text);
      return item;
    }

    async function loadStorageUsage(): Promise<void> {
      storageStatus.show("Loading storage usage\u2026");
      try {
        const libraries = await api.getStorageUsage();
        storageList.replaceChildren();
        if (libraries.length === 0) {
          storageStatus.show("Storage usage is empty.");
          return;
        }
        const list = el("ul", { className: "storage-list" });
        for (const lib of libraries) {
          for (const folder of lib.Folders) {
            list.append(buildListItem(lib.Name, folder.Path, folder.UsedSpace, folder.FreeSpace));
          }
        }
        storageList.append(list);
        storageStatus.show("Storage usage loaded.");
      } catch {
        storageList.replaceChildren();
        storageStatus.show("Failed to load storage usage.", "var(--is-error)");
      }
    }

    loadStorageUsage().catch(console.error);

    appendTabContent(container, supportSection.container, storageContainer);
  },
};
