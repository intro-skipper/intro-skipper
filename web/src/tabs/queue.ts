import { actionButton } from "../components/action-button.ts";
import { createStatusMessage } from "../components/async-feedback.ts";
import { confirmDashboard } from "../components/confirm-dialog.ts";
import { el } from "../components/dom.ts";
import * as api from "../store/api.ts";
import type { Tab } from "../types.ts";

const REFRESH_INTERVAL_MS = 3000;

function pluralize(count: number, noun: string): string {
    return `${count} ${noun}${count === 1 ? "" : "s"}`;
}

function formatUpdated(value: string | null): string | null {
    if (value === null) return null;
    const date = new Date(value);
    return Number.isNaN(date.valueOf()) ? value : date.toLocaleString();
}

function itemLabel(item: api.ScannerQueueItem): string {
    const title = item.series ? `${item.series} — ${item.name}` : item.name;
    return item.library ? `${title} (${item.library})` : title;
}

function activityLabel(job: api.ScannerQueueJob, activity: api.ScannerQueueActivity): string {
    const item = job.items.find((candidate) => candidate.itemId === activity.itemId);
    return item ? itemLabel(item) : activity.itemId;
}

function jobItems(job: api.ScannerQueueJob): HTMLElement[] {
    const remote = job.kind === "Remote analysis";
    const libraryPass = job.kind === "Library";
    const label = remote
        ? `${job.state === "Running" ? "Claimed" : "Queued"} batch: ${pluralize(job.items.length, "file")}.`
        : libraryPass
            ? job.items.length === 0 ? "Library pass: All selected libraries." : "Library pass: Known requested items:"
            : job.items.length === 0 ? "Requested items: none reported." : "Requested items:";
    const items: HTMLElement[] = [el("p", {}, label)];
    if (job.items.length > 0) {
        const list = el("ul");
        for (const item of job.items) {
            list.append(el("li", {}, itemLabel(item)));
        }
        items.push(list);
    }
    return items;
}

function jobDetails(job: api.ScannerQueueJob): HTMLElement {
    const details: Node[] = [
        el("p", {}, `${job.kind} · ${job.state} · ${job.nodeName} · ${job.modes.join(", ") || "No modes"}`),
        ...jobItems(job),
    ];
    const updated = formatUpdated(job.updatedUtc);
    if (updated !== null) {
        details.push(el("p", {}, `Completed files: ${job.completed.length}.`), el("p", {}, `Updated: ${updated}`));
    }
    return el("article", { className: "queue-job" }, el("h3", {}, `Job ${job.id}`), ...details);
}

function runningJob(job: api.ScannerQueueJob): HTMLElement {
    const active = job.active.length === 0
        ? [el("p", {}, "Preparing work; no active file has been reported.")]
        : job.active.map((activity) => el(
            "p",
            {},
            `Active file: ${activityLabel(job, activity)} · ${activity.mode} · ${activity.phase}`,
        ));
    return el(
        "article",
        { className: "queue-job" },
        el("h3", {}, `${job.nodeName} · ${job.kind}`),
        ...active,
        ...jobItems(job),
    );
}

function snapshotView(snapshot: api.ScannerQueueSnapshot): HTMLElement[] {
    const state = snapshot.paused ? "Paused" : snapshot.running.length > 0 ? "Running" : "Waiting";
    const activeSection = el("section", {}, el("h2", {}, "Active files"));
    activeSection.append(
        snapshot.running.length === 0
            ? el("p", {}, "No files are currently being analyzed.")
            : el("div", {}, ...snapshot.running.map(runningJob)),
    );

    const pendingSection = el("section", {}, el("h2", {}, "Pending jobs"));
    pendingSection.append(
        snapshot.pending.length === 0
            ? el("p", {}, "No jobs are waiting for a scanner.")
            : el("div", {}, ...snapshot.pending.map(jobDetails)),
    );

    const view: HTMLElement[] = [
        el(
            "p",
            { role: "status", "aria-live": "polite" },
            `${state} · ${pluralize(snapshot.totalPendingJobs, "pending job")}`,
        ),
        activeSection,
        pendingSection,
    ];
    if (snapshot.totalPendingJobs > snapshot.pending.length) {
        view.push(
            el(
                "p",
                { role: "status" },
                `Showing ${pluralize(snapshot.pending.length, "pending job")} of ${snapshot.totalPendingJobs}; the server limits this view to the first 100 jobs.`,
            ),
        );
    }
    return view;
}

export const queueTab: Tab = {
    id: "queue",
    label: "Queue",
    render(container, signal) {
        const content = el("div");
        const status = createStatusMessage();
        const controls = el("div", { className: "form-group" });
        const buttons: HTMLButtonElement[] = [];
        let refreshInFlight = false;
        let controlling = false;
        let confirmingClear = false;
        let version = 0;
        let refreshTimer: number | null = null;
        let refreshController = new AbortController();

        const setControlsDisabled = (disabled: boolean) => {
            for (const button of buttons) button.disabled = disabled;
        };

        const scheduleRefresh = () => {
            if (signal.aborted || controlling || refreshTimer !== null) return;
            refreshTimer = window.setTimeout(() => {
                refreshTimer = null;
                void refresh();
            }, REFRESH_INTERVAL_MS);
        };

        const cancelRefresh = () => {
            if (refreshTimer !== null) {
                window.clearTimeout(refreshTimer);
                refreshTimer = null;
            }
            refreshController.abort();
            refreshController = new AbortController();
        };

        const renderSnapshot = (snapshot: api.ScannerQueueSnapshot) => {
            content.replaceChildren(...snapshotView(snapshot));
        };

        const refresh = async () => {
            if (refreshInFlight || controlling || signal.aborted) return;
            refreshInFlight = true;
            const refreshVersion = version;
            const refreshSignal = refreshController.signal;
            try {
                const result = await api.getQueue(refreshSignal);
                if (signal.aborted || refreshSignal.aborted || refreshVersion !== version) return;
                if (!result.ok) {
                    status.show(`Could not load the queue: ${result.error}`, "error");
                    return;
                }
                renderSnapshot(result.data);
                status.clear();
            } catch (error) {
                if (!signal.aborted && !refreshSignal.aborted) {
                    status.show(
                        error instanceof Error ? `Could not load the queue: ${error.message}` : "Could not load the queue.",
                        "error",
                    );
                }
            } finally {
                refreshInFlight = false;
                if (!signal.aborted && !controlling) scheduleRefresh();
            }
        };

        const control = async (action: api.QueueControlAction) => {
            if (controlling || signal.aborted) return;
            controlling = true;
            version += 1;
            cancelRefresh();
            setControlsDisabled(true);
            status.show(action === "start" ? "Resuming queue processing…" : `Applying ${action}…`);
            try {
                const result = await api.controlQueue(action, signal);
                if (signal.aborted) return;
                if (!result.ok) {
                    status.show(`Could not ${action} the queue: ${result.error}`, "error");
                    return;
                }
                renderSnapshot(result.data);
                status.show(
                    action === "start"
                        ? "Queue processing resumed. No library scan was started."
                        : `Queue ${action} request applied.`,
                    "success",
                );
            } catch (error) {
                if (!signal.aborted) {
                    status.show(
                        error instanceof Error ? `Could not ${action} the queue: ${error.message}` : `Could not ${action} the queue.`,
                        "error",
                    );
                }
            } finally {
                controlling = false;
                if (!signal.aborted) {
                    setControlsDisabled(false);
                    scheduleRefresh();
                }
            }
        };

        const refreshButton = actionButton("Refresh", refresh) as HTMLButtonElement;
        const startButton = actionButton("Start", () => control("start")) as HTMLButtonElement;
        const pauseButton = actionButton("Pause", () => control("pause")) as HTMLButtonElement;
        const stopButton = actionButton("Stop", () => control("stop")) as HTMLButtonElement;
        const clearButton = actionButton("Clear pending", async () => {
            if (confirmingClear || controlling) return;
            confirmingClear = true;
            setControlsDisabled(true);
            try {
                if (
                    await confirmDashboard(
                        "Clear all pending queue jobs? Active files will continue, and accepted markers and node policies will not change.",
                        "Clear pending jobs",
                    )
                ) {
                    await control("clear");
                }
            } finally {
                confirmingClear = false;
                if (!controlling && !signal.aborted) setControlsDisabled(false);
            }
        }) as HTMLButtonElement;
        buttons.push(refreshButton, startButton, pauseButton, stopButton, clearButton);
        controls.append(...buttons);

        signal.addEventListener("abort", cancelRefresh, { once: true });

        container.append(
            el("h1", {}, "Queue"),
            el(
                "p",
                {},
                "Queue controls apply to existing local and remote jobs. Automatic detection only controls new library watcher events.",
            ),
            el(
                "p",
                {},
                "Pause blocks new jobs while active jobs finish. Stop pauses and cancels active work while retaining pending jobs. Clear removes pending jobs only.",
            ),
            controls,
            status.element,
            content,
        );
        content.append(el("p", { role: "status" }, "Loading queue…"));
        void refresh();
    },
};
