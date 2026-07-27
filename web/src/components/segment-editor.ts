import { el } from "./dom.ts";
import { confirmDialog } from "./confirm-dialog.ts";
import { parseTimeInput, formatTimeInput } from "../utils.ts";
import * as api from "../store/api.ts";
import type { SegmentDto, SegmentType } from "../types.ts";

/**
 * Inline multi-segment editor for one episode/movie: add, edit, delete and
 * restore segments through the plural segments API. Deleting an automatically
 * detected segment tombstones it server-side so re-analysis does not re-add it.
 */

const MODE_OPTIONS: ReadonlyArray<{ value: SegmentType; label: string }> = [
    { value: "Introduction", label: "Intro" },
    { value: "Credits", label: "Credits" },
    { value: "Recap", label: "Recap" },
    { value: "Preview", label: "Preview" },
    { value: "Commercial", label: "Commercial" },
];

const MODE_ORDER: Record<string, number> = {
    Introduction: 0,
    Credits: 1,
    Recap: 2,
    Preview: 3,
    Commercial: 4,
};

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
        (a, b) => (MODE_ORDER[a.Type] ?? 99) - (MODE_ORDER[b.Type] ?? 99) || a.Start - b.Start,
    );
}

export function segmentEditor(opts: {
    itemId: string;
    initialSegments: SegmentDto[];
    onChanged: (segments: SegmentDto[]) => void;
}): { container: HTMLElement; refresh: (segments: SegmentDto[]) => void; destroy: () => void } {
    const container = el("div", { className: "ts-segment-editor" });
    const rowsEl = el("div");
    const statusEl = el("div", { className: "ts-segment-status" });
    statusEl.setAttribute("aria-live", "polite");
    statusEl.style.display = "none";

    let destroyed = false;
    let busy = false;

    function setStatus(msg: string, color = "var(--is-text-muted)"): void {
        if (destroyed) return;
        statusEl.textContent = msg;
        statusEl.style.color = color;
        statusEl.style.display = msg ? "block" : "none";
    }

    async function reloadAfterMutation(successMessage: string): Promise<void> {
        const result = await api.getEpisodeSegments(opts.itemId);
        if (destroyed) return;
        if (result.ok) {
            const segments = result.data ?? [];
            renderRows(segments);
            opts.onChanged(segments);
            setStatus(successMessage, "var(--is-success)");
        } else {
            setStatus(result.error ?? "Failed to reload segments", "var(--is-error)");
        }
    }

    function overlapWarning(segments: SegmentDto[], type: SegmentType, start: number, end: number, excludeId?: string): string {
        const overlapping = segments.some(
            (s) => s.Type === type && s.Id !== excludeId && !s.Suppressed && start < s.End && s.Start < end,
        );
        // Overlaps are legal in the plural model; surface them as a warning only.
        return overlapping ? " Warning: overlaps another " + type + " segment." : "";
    }

    function buildRow(segment: SegmentDto, segments: SegmentDto[]): HTMLElement {
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
            restoreBtn.addEventListener("click", async () => {
                if (busy) return;
                busy = true;
                try {
                    const response = await api.restoreEpisodeSegment(opts.itemId, segment.Id);
                    if (destroyed) return;
                    if (response.ok) {
                        await reloadAfterMutation("Segment restored.");
                    } else {
                        setStatus(response.error ?? "Failed to restore segment", "var(--is-error)");
                    }
                } finally {
                    busy = false;
                }
            });
            row.append(startInput, endInput, badge, el("span", { className: "ts-segment-hint" }, "hidden"), restoreBtn, errorEl);
            return row;
        }

        const saveBtn = el("button", { className: "ts-segment-btn", type: "button" }, "Save");
        const deleteBtn = el("button", { className: "ts-segment-btn danger", type: "button" }, "Delete");

        saveBtn.addEventListener("click", async () => {
            if (busy) return;
            const start = parseTimeInput(startInput.value);
            const end = parseTimeInput(endInput.value);
            if (start === null || end === null) {
                errorEl.textContent = "Enter a time like 95.5 or 1:35.5";
                return;
            }
            if (end <= start) {
                errorEl.textContent = "End must be after start";
                return;
            }
            errorEl.textContent = "";
            busy = true;
            try {
                const result = await api.updateEpisodeSegment(opts.itemId, segment.Id, { Start: start, End: end });
                if (destroyed) return;
                if (result.ok) {
                    await reloadAfterMutation("Segment saved." + overlapWarning(segments, segment.Type, start, end, segment.Id));
                } else {
                    errorEl.textContent = result.error ?? "Failed to save segment";
                }
            } finally {
                busy = false;
            }
        });

        deleteBtn.addEventListener("click", async () => {
            if (busy) return;
            const confirmed = await confirmDialog({
                title: "Delete segment",
                body: segment.IsUserDefined
                    ? "This permanently deletes the segment."
                    : "This hides the automatically detected segment. Re-analysis will not re-add it. Erasing timestamps restores automatic detection.",
                confirmLabel: "Delete",
            });
            if (!confirmed || destroyed) return;
            busy = true;
            try {
                // deleteEpisodeSegment returns the raw fetch, which can reject on
                // network failure — the finally keeps the editor usable either way.
                const response = await api.deleteEpisodeSegment(opts.itemId, segment.Id);
                if (destroyed) return;
                if (response.ok) {
                    await reloadAfterMutation("Segment deleted.");
                } else {
                    setStatus("Failed to delete segment (HTTP " + response.status + ")", "var(--is-error)");
                    window.Dashboard.alert("Failed to delete segment");
                }
            } catch (err: unknown) {
                if (!destroyed) {
                    setStatus(err instanceof Error ? err.message : "Failed to delete segment", "var(--is-error)");
                }
            } finally {
                busy = false;
            }
        });

        row.append(startInput, endInput, badge, saveBtn, deleteBtn, errorEl);
        return row;
    }

    function buildAddRow(segments: SegmentDto[]): HTMLElement {
        const row = el("div", { className: "ts-segment-row ts-segment-add-row" });
        const select = el("select", { className: "ts-segment-select" }) as HTMLSelectElement;
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

        addBtn.addEventListener("click", async () => {
            if (busy) return;
            const start = parseTimeInput(startInput.value);
            const end = parseTimeInput(endInput.value);
            const type = select.value as SegmentType;
            if (start === null || end === null) {
                errorEl.textContent = "Enter a time like 95.5 or 1:35.5";
                return;
            }
            if (end <= start) {
                errorEl.textContent = "End must be after start";
                return;
            }
            errorEl.textContent = "";
            busy = true;
            try {
                const result = await api.createEpisodeSegment(opts.itemId, { Type: type, Start: start, End: end });
                if (destroyed) return;
                if (result.ok) {
                    startInput.value = "";
                    endInput.value = "";
                    await reloadAfterMutation("Segment added." + overlapWarning(segments, type, start, end));
                } else {
                    errorEl.textContent = result.error ?? "Failed to add segment";
                }
            } finally {
                busy = false;
            }
        });

        row.append(select, startInput, endInput, addBtn, errorEl);
        return row;
    }

    function renderRows(segments: SegmentDto[]): void {
        rowsEl.replaceChildren();
        const sorted = sortSegments(segments);
        for (const segment of sorted) {
            rowsEl.append(buildRow(segment, sorted));
        }
        rowsEl.append(buildAddRow(sorted));
    }

    container.append(rowsEl, statusEl);
    renderRows(opts.initialSegments);

    return {
        container,
        refresh(segments: SegmentDto[]) {
            renderRows(segments);
        },
        destroy() {
            destroyed = true;
            container.replaceChildren();
        },
    };
}
