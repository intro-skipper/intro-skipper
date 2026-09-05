import { el } from "./dom.ts";
import { confirmDialog } from "./confirm-dialog.ts";
import { createStatusMessage } from "./async-feedback.ts";
import { parseTimeInput, formatTimeInput } from "../utils.ts";
import * as api from "../store/api.ts";
import type { SegmentDto, SegmentType } from "../types.ts";

/**
 * Inline multi-segment editor for one episode/movie: add, edit, delete and
 * restore segments through the plural segments API. Deleting an automatically
 * detected segment tombstones it server-side so re-analysis does not re-add it.
 */

export const MODE_OPTIONS: ReadonlyArray<{ value: SegmentType; label: string }> = [
    { value: "Introduction", label: "Intro" },
    { value: "Credits", label: "Credits" },
    { value: "Recap", label: "Recap" },
    { value: "Preview", label: "Preview" },
    { value: "Commercial", label: "Commercial" },
];

const MODE_ORDER: ReadonlyMap<string, number> = new Map(MODE_OPTIONS.map((m, i) => [m.value, i]));

export function sourceBadgeText(segment: SegmentDto): string {
    switch (segment.Source) {
        case "User":
            return "user";
        case "Chapter":
            return "chapter";
        case "Chromaprint":
            return "audio";
        case "BlackFrame":
            return "black frame";
        case "CreditsDerived":
            return "derived";
        default:
            return segment.Source.toLowerCase();
    }
}

export function sortSegments(segments: SegmentDto[]): SegmentDto[] {
    return [...segments].sort(
        (a, b) => (MODE_ORDER.get(a.Type) ?? 99) - (MODE_ORDER.get(b.Type) ?? 99) || a.Start - b.Start,
    );
}

function readRange(
    startInput: HTMLInputElement,
    endInput: HTMLInputElement,
    errorEl: HTMLElement,
): { start: number; end: number } | null {
    const start = parseTimeInput(startInput.value);
    const end = parseTimeInput(endInput.value);
    if (start === null || end === null) {
        errorEl.textContent = "Enter a time like 95.5 or 1:35.5";
        return null;
    }
    if (end <= start) {
        errorEl.textContent = "End must be after start";
        return null;
    }
    errorEl.textContent = "";
    return { start, end };
}

export function segmentEditor(opts: {
    itemId: string;
    initialSegments: SegmentDto[];
    onChanged: (segments: SegmentDto[]) => void;
}): { container: HTMLElement; isDirty: () => boolean; destroy: () => void } {
    const container = el("div", { className: "ts-segment-editor" });
    const rowsEl = el("div");
    const status = createStatusMessage({ className: "ts-segment-status", display: "block" });

    let destroyed = false;
    let busy = false;
    // Last rendered segment list; used to compute a local fallback view when a
    // post-mutation reload fails.
    let current = opts.initialSegments;
    // Rebuilt alongside the rows; each entry reports whether its row's inputs
    // differ from the rendered segment (or, for the add row, hold typed input).
    let dirtyChecks: Array<() => boolean> = [];

    function setStatus(msg: string, color = "var(--is-text-muted)"): void {
        if (destroyed) return;
        status.show(msg, color);
    }

    // Serializes mutations: while one is in flight, further clicks are ignored.
    async function withBusy(fn: () => Promise<void>): Promise<void> {
        if (busy) return;
        busy = true;
        try {
            await fn();
        } finally {
            busy = false;
        }
    }

    /**
     * Refreshes rows and pills after a mutation that already succeeded. When
     * the follow-up GET fails, renders `fallback` (the locally computed result
     * of the mutation) instead of leaving stale rows whose buttons would act
     * on segments that no longer exist; the next successful reload self-heals.
     */
    async function reloadAfterMutation(
        successMessage: string,
        fallback: SegmentDto[],
        overlap?: { type: SegmentType; start: number; end: number; excludeId?: string },
    ): Promise<void> {
        const result = await api.getEpisodeSegments(opts.itemId);
        if (destroyed) return;
        const segments = result.ok ? (result.data ?? []) : fallback;
        renderRows(segments);
        opts.onChanged(segments);
        if (result.ok) {
            setStatus(successMessage + overlapWarning(segments, overlap), "var(--is-success)");
        } else {
            setStatus(
                successMessage + " Reloading failed; showing the unverified local result.",
                "var(--is-error)",
            );
        }
    }

    function overlapWarning(
        segments: SegmentDto[],
        overlap?: { type: SegmentType; start: number; end: number; excludeId?: string },
    ): string {
        if (!overlap) return "";
        const overlapping = segments.some(
            (s) =>
                s.Type === overlap.type &&
                s.Id !== overlap.excludeId &&
                !s.Suppressed &&
                overlap.start < s.End &&
                s.Start < overlap.end,
        );
        // Overlaps are legal in the plural model; surface them as a warning only.
        return overlapping ? " Warning: overlaps another " + overlap.type + " segment." : "";
    }

    function buildRow(segment: SegmentDto): HTMLElement {
        const row = el("div", { className: "ts-segment-row" + (segment.Suppressed ? " suppressed" : "") });
        const label = MODE_OPTIONS.find((m) => m.value === segment.Type)?.label ?? segment.Type;
        row.append(el("span", { className: "ts-segment-mode" }, label));

        const startInput = el("input", {
            className: "ts-segment-input",
            type: "text",
            value: formatTimeInput(segment.Start),
        });
        startInput.setAttribute("aria-label", label + " start");
        const endInput = el("input", {
            className: "ts-segment-input",
            type: "text",
            value: formatTimeInput(segment.End),
        });
        endInput.setAttribute("aria-label", label + " end");

        const badge = el("span", { className: "ts-pill-source" }, sourceBadgeText(segment));
        const errorEl = el("span", { className: "ts-segment-error" });

        if (segment.Suppressed) {
            startInput.disabled = true;
            endInput.disabled = true;
            const restoreBtn = el("button", { className: "ts-segment-btn", type: "button" }, "Restore");
            restoreBtn.addEventListener("click", () => withBusy(async () => {
                const response = await api.restoreEpisodeSegment(opts.itemId, segment.Id);
                if (destroyed) return;
                if (response.ok) {
                    const next = current.map((s) =>
                        s.Id === segment.Id ? { ...s, Suppressed: false } : s,
                    );
                    await reloadAfterMutation("Segment restored.", next);
                } else {
                    setStatus(response.error ?? "Failed to restore segment", "var(--is-error)");
                }
            }));
            row.append(startInput, endInput, badge, el("span", { className: "ts-segment-hint" }, "hidden"), restoreBtn, errorEl);
            return row;
        }

        dirtyChecks.push(
            () =>
                startInput.value !== formatTimeInput(segment.Start) ||
                endInput.value !== formatTimeInput(segment.End),
        );

        const saveBtn = el("button", { className: "ts-segment-btn", type: "button" }, "Save");
        const deleteBtn = el("button", { className: "ts-segment-btn danger", type: "button" }, "Delete");

        saveBtn.addEventListener("click", () => withBusy(async () => {
            const range = readRange(startInput, endInput, errorEl);
            if (range === null) return;
            const result = await api.updateEpisodeSegment(opts.itemId, segment.Id, { Start: range.start, End: range.end });
            if (destroyed) return;
            if (result.ok) {
                const next = current.map((s) =>
                    s.Id === segment.Id ? { ...s, Start: range.start, End: range.end } : s,
                );
                await reloadAfterMutation("Segment saved.", next, {
                    type: segment.Type,
                    start: range.start,
                    end: range.end,
                    excludeId: segment.Id,
                });
            } else {
                errorEl.textContent = result.error ?? "Failed to save segment";
            }
        }));

        deleteBtn.addEventListener("click", async () => {
            if (busy) return;
            const confirmed = await confirmDialog({
                title: "Delete segment",
                body: segment.Source === "User"
                    ? "This permanently deletes the segment."
                    : "This hides the automatically detected segment. Re-analysis will not re-add it. Erasing timestamps restores automatic detection.",
                confirmLabel: "Delete",
            });
            if (!confirmed || destroyed) return;
            await withBusy(async () => {
                const result = await api.deleteEpisodeSegment(opts.itemId, segment.Id);
                if (destroyed) return;
                if (result.ok) {
                    // Mirrors the server's delete rule (see confirm text above):
                    // user segments are removed, automatic ones are tombstoned.
                    const next = segment.Source === "User"
                        ? current.filter((s) => s.Id !== segment.Id)
                        : current.map((s) => (s.Id === segment.Id ? { ...s, Suppressed: true } : s));
                    await reloadAfterMutation("Segment deleted.", next);
                } else {
                    setStatus(result.error ?? "Failed to delete segment", "var(--is-error)");
                }
            });
        });

        row.append(startInput, endInput, badge, saveBtn, deleteBtn, errorEl);
        return row;
    }

    function buildAddRow(): HTMLElement {
        const row = el("div", { className: "ts-segment-row ts-segment-add-row" });
        const select = el("select", { className: "ts-segment-select" });
        for (const mode of MODE_OPTIONS) {
            select.append(el("option", { value: mode.value }, mode.label));
        }
        select.setAttribute("aria-label", "New segment type");

        const startInput = el("input", { className: "ts-segment-input", type: "text", placeholder: "start" });
        startInput.setAttribute("aria-label", "New segment start");
        const endInput = el("input", { className: "ts-segment-input", type: "text", placeholder: "end" });
        endInput.setAttribute("aria-label", "New segment end");
        const errorEl = el("span", { className: "ts-segment-error" });
        const addBtn = el("button", { className: "ts-segment-btn", type: "button" }, "Add");

        dirtyChecks.push(() => startInput.value.trim() !== "" || endInput.value.trim() !== "");

        addBtn.addEventListener("click", () => withBusy(async () => {
            const range = readRange(startInput, endInput, errorEl);
            if (range === null) return;
            const type = select.value as SegmentType;
            const result = await api.createEpisodeSegment(opts.itemId, { Type: type, Start: range.start, End: range.end });
            if (destroyed) return;
            if (result.ok) {
                // reloadAfterMutation always re-renders, so the add row (and its
                // inputs) is rebuilt empty either way.
                const created = result.data;
                const next = created ? [...current, created] : current;
                await reloadAfterMutation("Segment added.", next, {
                    type,
                    start: range.start,
                    end: range.end,
                    excludeId: created?.Id,
                });
            } else {
                errorEl.textContent = result.error ?? "Failed to add segment";
            }
        }));

        row.append(select, startInput, endInput, addBtn, errorEl);
        return row;
    }

    function renderRows(segments: SegmentDto[]): void {
        current = segments;
        dirtyChecks = [];
        rowsEl.replaceChildren();
        for (const segment of sortSegments(segments)) {
            rowsEl.append(buildRow(segment));
        }
        rowsEl.append(buildAddRow());
    }

    container.append(rowsEl, status.element);
    renderRows(opts.initialSegments);

    return {
        container,
        // True while any editable row's inputs differ from the rendered segment
        // or the add row holds typed input; callers use this to avoid re-renders
        // that would discard unsaved edits.
        isDirty() {
            return !destroyed && dirtyChecks.some((check) => check());
        },
        destroy() {
            destroyed = true;
            container.replaceChildren();
        },
    };
}
