import { el } from "./dom.ts";
import { formatTime } from "../utils.ts";
import * as api from "../store/api.ts";
import { getImageUrl } from "../store/jellyfin-client.ts";
import { MODE_OPTIONS, segmentEditor, sortSegments, sourceBadgeText } from "./segment-editor.ts";
import type { EpisodeItem, SegmentDto, ApiResult } from "../types.ts";

/** Delay before filtering the episode list (ms). */
const FILTER_DEBOUNCE_MS = 120;

export function episodeList(): {
    container: HTMLElement;
    render: (
        episodes: EpisodeItem[],
        segments: Array<ApiResult<SegmentDto[]> | null>,
        isMovie?: boolean,
        disabledItemIds?: string[],
        onDisabledChange?: (itemId: string, disabled: boolean) => Promise<void>,
    ) => void;
    clear: () => void;
    setStatus: (msg: string, color?: string) => void;
    destroy: () => void;
} {
    const container = el("div");

    const filterBar = el("div", { className: "ts-filter-bar" });
    const filterInput = el("input", {
        className: "ts-filter-input",
        type: "text",
        placeholder: "Filter episodes…",
        name: "episode-filter",
    });
    filterInput.setAttribute("aria-label", "Filter episodes by name");
    filterInput.setAttribute("autocomplete", "off");
    const countEl = el("span", { className: "ts-episode-count" });
    countEl.setAttribute("aria-live", "polite");
    filterBar.append(filterInput, countEl);

    const statusEl = el("div", { className: "ts-status-msg" });
    statusEl.style.display = "none";
    statusEl.setAttribute("aria-live", "polite");

    const listEl = el("div");

    container.append(filterBar, statusEl, listEl);

    let currentEpisodes: EpisodeItem[] = [];
    let currentCards: HTMLElement[] = [];
    let filterTimer: ReturnType<typeof setTimeout> | null = null;
    let editors: Array<{ destroy: () => void }> = [];
    let editorCounter = 0;

    function ticksToMinutes(ticks: number | null): string {
        if (!ticks) return "";
        const minutes = Math.round(ticks / 10_000_000 / 60);
        return minutes + " min";
    }

    let isMovieView = false;
    let currentDisabledIds = new Set<string>();
    let onDisabledChange: ((itemId: string, disabled: boolean) => Promise<void>) | null = null;

    function buildCard(
        ep: EpisodeItem,
        result: ApiResult<SegmentDto[]> | null,
        index: number,
    ): HTMLElement {
        const card = el("div", { className: "ts-episode-card" });

        const img = el("img", {
            className: "ts-episode-thumb",
            src: getImageUrl(ep.Id),
            alt: "",
            width: "64",
            height: "38",
        });
        img.loading = "lazy";
        img.onerror = () => {
            img.style.display = "none";
        };
        card.append(img);

        const info = el("div", { className: "ts-episode-info" });

        const header = el("div", { className: "ts-episode-header" });
        const prefix = isMovieView
            ? ""
            : (ep.IndexNumber ?? index + 1).toLocaleString(undefined, { minimumIntegerDigits: 2 }) +
              ": ";
        header.append(el("span", { className: "ts-episode-name" }, prefix + ep.Name));
        const runtime = ticksToMinutes(ep.RunTimeTicks);
        if (runtime) {
            header.append(el("span", { className: "ts-episode-runtime" }, runtime));
        }
        if (onDisabledChange) {
            attachDisableToggle(ep, card, header);
        }
        info.append(header);

        if (!result || !result.ok) {
            card.classList.add("error");
            const errorDiv = el("div", { className: "ts-episode-error" });
            errorDiv.append(document.createTextNode("Failed to load segments"));

            const retryBtn = el("button", { className: "ts-retry-link", type: "button" }, "Retry");
            retryBtn.setAttribute("aria-label", "Retry loading segments for " + ep.Name);
            retryBtn.addEventListener("click", async () => {
                // Retry only this episode so one failed request does not force a full reload.
                retryBtn.textContent = "Loading…";
                retryBtn.style.pointerEvents = "none";
                const retryResult = await api.getEpisodeSegments(ep.Id);
                if (retryResult && retryResult.ok) {
                    card.classList.remove("error");
                    info.removeChild(errorDiv);
                    attachSegmentUi(ep, header, info, retryResult.data ?? []);
                } else {
                    retryBtn.textContent = "Retry";
                    retryBtn.style.pointerEvents = "";
                }
            });
            errorDiv.append(retryBtn);
            info.append(errorDiv);
        } else {
            attachSegmentUi(ep, header, info, result.data ?? []);
        }

        card.append(info);
        return card;
    }

    /**
     * Renders the segment pill row plus a lazily-created inline editor toggled by
     * an Edit button in the header. Mutations refresh the pills in place — no
     * full-season reload.
     */
    function attachSegmentUi(
        ep: EpisodeItem,
        header: HTMLElement,
        info: HTMLElement,
        segments: SegmentDto[],
    ): void {
        let pillsRow = buildSegmentPills(segments);
        info.append(pillsRow);

        const editorId = "ts-segment-editor-" + ++editorCounter;
        const editBtn = el("button", { className: "ts-edit-btn", type: "button" }, "Edit");
        editBtn.setAttribute("aria-expanded", "false");
        editBtn.setAttribute("aria-controls", editorId);
        header.append(editBtn);

        let editor: ReturnType<typeof segmentEditor> | null = null;
        editBtn.addEventListener("click", () => {
            if (!editor) {
                editor = segmentEditor({
                    itemId: ep.Id,
                    initialSegments: segments,
                    onChanged: (next) => {
                        const fresh = buildSegmentPills(next);
                        pillsRow.replaceWith(fresh);
                        pillsRow = fresh;
                    },
                });
                editor.container.id = editorId;
                info.append(editor.container);
                editors.push(editor);
                editBtn.setAttribute("aria-expanded", "true");
                return;
            }

            const visible = editor.container.style.display !== "none";
            editor.container.style.display = visible ? "none" : "";
            editBtn.setAttribute("aria-expanded", String(!visible));
        });
    }

    /**
     * Pill switch controlling whether the item's automatic segments reach
     * Jellyfin. Checked means enabled; disabled items keep their stored
     * segments and dim the card.
     */
    function attachDisableToggle(ep: EpisodeItem, card: HTMLElement, header: HTMLElement): void {
        const toggle = el("input", { className: "ts-episode-disable-toggle", type: "checkbox" });
        const disabled = currentDisabledIds.has(ep.Id);
        toggle.checked = !disabled;
        toggle.setAttribute("aria-label", "Enable media segments for " + ep.Name);
        toggle.title = "Turn off to hide this item's detected segments from Jellyfin";
        card.classList.toggle("ts-episode-disabled", disabled);

        toggle.addEventListener("change", async () => {
            toggle.disabled = true;
            const nowDisabled = !toggle.checked;
            try {
                await onDisabledChange?.(ep.Id, nowDisabled);
                if (nowDisabled) {
                    currentDisabledIds.add(ep.Id);
                } else {
                    currentDisabledIds.delete(ep.Id);
                }
                card.classList.toggle("ts-episode-disabled", nowDisabled);
            } catch {
                toggle.checked = !toggle.checked;
                window.Dashboard.alert("Failed to update media-segment setting");
            } finally {
                toggle.disabled = false;
            }
        });

        header.append(toggle);
    }

    function buildSegmentPills(segments: SegmentDto[]): HTMLElement {
        const row = el("div", { className: "ts-episode-timestamps" });
        const active = sortSegments(segments.filter((s) => !s.Suppressed));
        for (const mode of MODE_OPTIONS) {
            const ofMode = active.filter((s) => s.Type === mode.value);
            if (ofMode.length === 0) {
                row.append(
                    el("span", { className: "ts-timestamp-missing" }, mode.label + " –"),
                );
                continue;
            }

            for (const seg of ofMode) {
                const pill = el(
                    "span",
                    { className: "ts-timestamp-pill" + (seg.Source === "User" ? " user" : "") },
                    mode.label + " " + formatTime(seg.Start) + " – " + formatTime(seg.End),
                );
                pill.append(el("span", { className: "ts-pill-source" }, sourceBadgeText(seg)));
                row.append(pill);
            }
        }
        return row;
    }

    function applyFilter(): void {
        const query = filterInput.value.toLowerCase();
        let visibleCount = 0;
        currentCards.forEach((card, i) => {
            const name = currentEpisodes[i]?.Name?.toLowerCase() ?? "";
            const visible = !query || name.includes(query);
            card.style.display = visible ? "" : "none";
            if (visible) visibleCount++;
        });

        if (query && visibleCount === 0) {
            countEl.textContent = "No matching episodes";
            return;
        }

        countEl.textContent = visibleCount + " episode" + (visibleCount !== 1 ? "s" : "");
    }

    const handleFilterInput = () => {
        if (filterTimer) clearTimeout(filterTimer);
        filterTimer = setTimeout(() => {
            applyFilter();
        }, FILTER_DEBOUNCE_MS);
    };

    filterInput.addEventListener("input", handleFilterInput);

    function destroyEditors(): void {
        for (const editor of editors) {
            editor.destroy();
        }
        editors = [];
    }

    return {
        container,

        render(
            episodes: EpisodeItem[],
            segments: Array<ApiResult<SegmentDto[]> | null>,
            isMovie = false,
            disabledItemIds: string[] = [],
            onDisabledChangeCallback?: (itemId: string, disabled: boolean) => Promise<void>,
        ) {
            isMovieView = isMovie;
            currentDisabledIds = new Set(disabledItemIds);
            onDisabledChange = onDisabledChangeCallback ?? null;
            currentEpisodes = episodes;
            listEl.replaceChildren();
            destroyEditors();
            currentCards = [];
            if (filterTimer) clearTimeout(filterTimer);
            filterInput.value = "";

            if (episodes.length === 0) {
                listEl.append(el("div", { className: "ts-status-msg" }, "No episodes found."));
                countEl.textContent = "";
                return;
            }

            for (let i = 0; i < episodes.length; i++) {
                const card = buildCard(episodes[i], segments[i] ?? null, i);
                currentCards.push(card);
                listEl.append(card);
            }

            countEl.textContent = episodes.length + " episode" + (episodes.length !== 1 ? "s" : "");
            statusEl.style.display = "none";
        },

        clear() {
            listEl.replaceChildren();
            destroyEditors();
            currentCards = [];
            currentEpisodes = [];
            currentDisabledIds = new Set();
            onDisabledChange = null;
            if (filterTimer) clearTimeout(filterTimer);
            countEl.textContent = "";
            filterInput.value = "";
        },

        setStatus(msg: string, color = "var(--is-text-muted)") {
            if (!msg) {
                statusEl.style.display = "none";
                statusEl.textContent = "";
                return;
            }
            statusEl.textContent = msg;
            statusEl.style.color = color;
            statusEl.style.display = "block";
        },

        destroy() {
            if (filterTimer) {
                clearTimeout(filterTimer);
                filterTimer = null;
            }
            destroyEditors();
            filterInput.removeEventListener("input", handleFilterInput);
        },
    };
}
