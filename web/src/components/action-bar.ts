import { el } from "./dom.ts";
import { bindStatusMessage, withDashboardLoading } from "./async-feedback.ts";
import { confirmDialog } from "./confirm-dialog.ts";
import * as api from "../store/api.ts";
import type { AnalyzerActions } from "../types.ts";
import { delay } from "../utils.ts";

const ANALYZER_ACTION_ORDER: ReadonlyArray<{
  key: string;
  label: string;
  options: ReadonlyArray<{ value: string; label: string }>;
}> = [
  {
    key: "Recap",
    label: "Recap",
    options: [
      { value: "Default", label: "Default" },
      { value: "Chapter", label: "Chapter" },
      { value: "None", label: "None" },
    ],
  },
  {
    key: "Introduction",
    label: "Intro",
    options: [
      { value: "Default", label: "Default" },
      { value: "Chapter", label: "Chapter" },
      { value: "Chromaprint", label: "Chromaprint" },
      { value: "None", label: "None" },
    ],
  },
  {
    key: "Credits",
    label: "Credits",
    options: [
      { value: "Default", label: "Default" },
      { value: "Chapter", label: "Chapter" },
      { value: "Chromaprint", label: "Chromaprint" },
      { value: "BlackFrame", label: "BlackFrame" },
      { value: "None", label: "None" },
    ],
  },
  {
    key: "Preview",
    label: "Preview",
    options: [
      { value: "Default", label: "Default" },
      { value: "Chapter", label: "Chapter" },
      { value: "None", label: "None" },
    ],
  },
  {
    key: "Commercial",
    label: "Commercial",
    options: [
      { value: "Default", label: "Default" },
      { value: "Chapter", label: "Chapter" },
      { value: "None", label: "None" },
    ],
  },
];

const SEGMENT_EDITOR_PLUGIN_ID = "ace21d44a4e54a85ae75acd2e24a9574";

export type ActionBarOptions = {
  onScanComplete: () => void | Promise<void>;
};

export function actionBar(opts: ActionBarOptions): {
  container: HTMLElement;
  toggle: (open: boolean) => void;
  loadForSeason: (showId: string, seasonId: string, isMovie: boolean) => Promise<void>;
  destroy: () => void;
} {
  const container = el("div", { className: "ts-action-bar" });
  container.id = "ts-action-panel";

  const actionSelects: Record<string, HTMLSelectElement> = {};
  const analyzerGroup = el("div", { className: "ts-analyzer-group" });

  for (const action of ANALYZER_ACTION_ORDER) {
    const item = el("div", { className: "ts-analyzer-item" });
    const selectId = "ts-analyzer-" + action.key.toLowerCase();
    const labelEl = el("label", { className: "ts-analyzer-label", for: selectId }, action.label);
    const select = el("select", {
      id: selectId,
      name: "analyzer-" + action.key.toLowerCase(),
    }) as HTMLSelectElement;
    for (const opt of action.options) {
      select.append(el("option", { value: opt.value }, opt.label));
    }
    actionSelects[action.key] = select;
    item.append(labelEl);
    item.append(select);
    analyzerGroup.append(item);
  }

  const applyBtn = el(
    "button",
    { className: "ts-action-btn apply", type: "button" },
    "Save Analyzer Overrides",
  );
  const scanBtn = el("button", { className: "ts-action-btn scan", type: "button" }, "Scan Season");
  const eraseBtn = el(
    "button",
    { className: "ts-action-btn erase", type: "button" },
    "Erase Season Timestamps",
  );

  const buttonsDiv = el("div", { className: "ts-action-buttons" });
  buttonsDiv.append(applyBtn, scanBtn, eraseBtn);

  const row = el("div", { className: "ts-action-row" });
  row.append(analyzerGroup, buttonsDiv);

  const metaRow = el("div", { className: "ts-action-meta" });
  const statusEl = el("div", { className: "ts-action-status" });
  const statusMessage = bindStatusMessage(statusEl, { display: "block" });
  const editorLink = el(
    "a",
    {
      href: "#/dashboard/plugins/" + SEGMENT_EDITOR_PLUGIN_ID + "?name=Segment Editor",
    },
    "Segment Editor \u2192",
  );
  metaRow.append(editorLink);

  container.append(row, metaRow, statusEl);

  let currentShowId = "";
  let currentSeasonId = "";
  let currentIsMovie = false;
  let destroyed = false;
  let loadVersion = 0;
  let scanVersion = 0;

  function updateActionLabels(): void {
    scanBtn.textContent = currentIsMovie ? "Scan Movie" : "Scan Season";
    eraseBtn.textContent = currentIsMovie ? "Erase Movie Timestamps" : "Erase Season Timestamps";
  }

  function resetScanButton(): void {
    scanBtn.disabled = false;
    updateActionLabels();
  }

  const handleApplyClick = async () => {
    if (destroyed) return;

    statusMessage.show("Saving analyzer overrides\u2026", "var(--is-text-muted)");

    try {
      await withDashboardLoading(async () => {
        const actions: AnalyzerActions = {
          Introduction: actionSelects["Introduction"].value,
          Credits: actionSelects["Credits"].value,
          Recap: actionSelects["Recap"].value,
          Preview: actionSelects["Preview"].value,
          Commercial: actionSelects["Commercial"].value,
        };
        await api.updateAnalyzerActions(currentSeasonId, actions);
      });
      statusMessage.show("Analyzer overrides updated.", "var(--is-success)");
      window.Dashboard.alert("Analyzer actions updated");
    } catch {
      statusMessage.show("Failed to update analyzer overrides.", "var(--is-error)");
      window.Dashboard.alert("Failed to update analyzer actions");
    }
  };

  const pollForScanCompletion = async (scanToken: number): Promise<void> => {
    const MAX_POLL_ATTEMPTS = 300; // ~10 minutes with base interval
    const BASE_INTERVAL = 1000;
    const MAX_INTERVAL = 10_000;

    let attempts = 0;
    let interval = BASE_INTERVAL;

    while (!destroyed && scanToken === scanVersion && attempts < MAX_POLL_ATTEMPTS) {
      await delay(interval);
      if (destroyed || scanToken !== scanVersion) {
        return;
      }

      attempts++;
      const status = await api.getScanStatus();

      if (destroyed || scanToken !== scanVersion) {
        return;
      }

      if (status.ok && !status.data?.isRunning) {
        resetScanButton();
        statusMessage.show("Scan finished. Results refreshed.", "var(--is-success)");
        await Promise.resolve(opts.onScanComplete());
        return;
      }

      if (!status.ok) {
        interval = Math.min(interval * 2, MAX_INTERVAL);
      } else {
        interval = BASE_INTERVAL;
      }
    }

    if (destroyed || scanToken !== scanVersion) {
      return;
    }

    resetScanButton();
    statusMessage.show(
      "Scan status polling timed out. Refresh to check results.",
      "var(--is-warning)",
    );
    window.Dashboard.alert("Scan status polling timed out. Refresh to check results.");
  };

  const handleScanClick = async () => {
    if (destroyed) return;

    const scanToken = ++scanVersion;
    scanBtn.disabled = true;
    statusMessage.show("Starting scan\u2026", "var(--is-text-muted)");
    try {
      const response = await withDashboardLoading(async () => {
        const seasonId = currentIsMovie ? currentShowId : currentSeasonId;
        return api.scanSeason(currentShowId, seasonId);
      });

      if (destroyed || scanToken !== scanVersion) {
        return;
      }

      if (response.status === 409) {
        statusMessage.show("A scan is already in progress.", "var(--is-warning)");
        window.Dashboard.alert("A scan is already in progress.");
      } else if (!response.ok) {
        resetScanButton();
        statusMessage.show("Unable to start the scan.", "var(--is-error)");
        window.Dashboard.alert("Unable to start the scan.");
        return;
      }

      scanBtn.textContent = "Scan in progress\u2026";
      statusMessage.show(
        "Scan in progress\u2026 This can take several minutes.",
        "var(--is-text-muted)",
      );

      void pollForScanCompletion(scanToken).catch(console.error);
    } catch {
      resetScanButton();
      statusMessage.show("Unable to start the scan.", "var(--is-error)");
      window.Dashboard.alert("Unable to start the scan.");
    }
  };

  const handleEraseClick = async () => {
    if (destroyed) return;

    const label = currentIsMovie ? "movie" : "season";
    const url = currentIsMovie
      ? "Intros/Show/" + encodeURIComponent(currentShowId)
      : "Intros/Show/" +
        encodeURIComponent(currentShowId) +
        "/" +
        encodeURIComponent(currentSeasonId);
    const result = await confirmDialog({
      title: "Confirm Timestamp Erasure",
      body: "Are you sure you want to erase all timestamps for this " + label + "?",
      confirmLabel: "Erase",
      checkbox: { label: "Include cached fingerprints" },
    });
    if (destroyed) return;
    if (!result) return;
    statusMessage.show("Erasing timestamps\u2026", "var(--is-text-muted)");
    try {
      const response = await api.eraseItemTimestamps(url, result.checkboxChecked);
      if (!response.ok) {
        statusMessage.show("Failed to erase timestamps.", "var(--is-error)");
        window.Dashboard.alert("Failed to erase timestamps");
        return;
      }
      statusMessage.show("Timestamps erased.", "var(--is-success)");
      window.Dashboard.alert("Timestamps erased");
      await Promise.resolve(opts.onScanComplete());
    } catch {
      statusMessage.show("Failed to erase timestamps.", "var(--is-error)");
      window.Dashboard.alert("Failed to erase timestamps");
    }
  };

  async function resolveEditorLink(): Promise<void> {
    try {
      const plugins = await api.checkPlugins();
      if (destroyed) {
        return;
      }

      const isActive = plugins.some(
        (p) => p.Id === SEGMENT_EDITOR_PLUGIN_ID && p.Status === "Active",
      );
      if (isActive) {
        editorLink.setAttribute("href", "#/configurationpage?name=Segment%20Editor");
      }
    } catch {
      // Leave the generic plugin page link in place if plugin lookup fails.
    }
  }

  // Resolve the editor link once at construction time so it's ready
  // before the user navigates to a specific season.
  resolveEditorLink().catch(console.error);

  applyBtn.addEventListener("click", handleApplyClick);
  scanBtn.addEventListener("click", handleScanClick);
  eraseBtn.addEventListener("click", handleEraseClick);

  return {
    container,

    toggle(open: boolean) {
      container.classList.toggle("open", open);
    },

    async loadForSeason(showId: string, seasonId: string, isMovie: boolean) {
      if (destroyed) return;

      const loadToken = ++loadVersion;
      scanVersion += 1;
      currentShowId = showId;
      currentSeasonId = seasonId;
      currentIsMovie = isMovie;

      resetScanButton();
      statusMessage.clear();

      // Analyzer overrides only apply to seasons, not single movies.
      analyzerGroup.style.display = isMovie ? "none" : "";
      applyBtn.style.display = isMovie ? "none" : "";

      if (!isMovie) {
        const result = await api.getAnalyzerActions(seasonId);
        if (destroyed || loadToken !== loadVersion) {
          return;
        }

        const actions: AnalyzerActions = result.ok && result.data ? result.data : {};
        actionSelects["Introduction"].value = actions.Introduction ?? "Default";
        actionSelects["Credits"].value = actions.Credits ?? "Default";
        actionSelects["Recap"].value = actions.Recap ?? "Default";
        actionSelects["Preview"].value = actions.Preview ?? "Default";
        actionSelects["Commercial"].value = actions.Commercial ?? "Default";
      }

      // Disable the button if another scan is already running server-side.
      const status = await api.getScanStatus();
      if (destroyed || loadToken !== loadVersion) {
        return;
      }

      if (status.ok && status.data?.isRunning) {
        scanBtn.disabled = true;
        scanBtn.textContent = "Scan in progress\u2026";
        statusMessage.show(
          "Scan in progress\u2026 This can take several minutes.",
          "var(--is-text-muted)",
        );
      }
    },

    destroy() {
      destroyed = true;
      loadVersion += 1;
      scanVersion += 1;
      applyBtn.removeEventListener("click", handleApplyClick);
      scanBtn.removeEventListener("click", handleScanClick);
      eraseBtn.removeEventListener("click", handleEraseClick);
    },
  };
}
