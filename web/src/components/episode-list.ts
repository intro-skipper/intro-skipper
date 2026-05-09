import { el } from "./dom.ts";
import { formatTime } from "../utils.ts";
import * as api from "../store/api.ts";
import { getImageUrl } from "../store/jellyfin-client.ts";
import type { EpisodeItem, TimestampMap, ApiResult } from "../types.ts";
import { t } from "../i18n/index.ts";

/** Delay before filtering the episode list (ms). */
const FILTER_DEBOUNCE_MS = 120;

const TIMESTAMP_MODES: ReadonlyArray<{ key: string; label: string }> = [
    { key: "Introduction", label: "Intro" },
    { key: "Credits", label: "Credits" },
    { key: "Recap", label: "Recap" },
    { key: "Preview", label: "Preview" },
    { key: "Commercial", label: "Commercial" },
];

export function episodeList(): {
    container: HTMLElement;
    render: (
        episodes: EpisodeItem[],
        timestamps: Array<ApiResult<TimestampMap> | null>,
        isMovie?: boolean,
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
        placeholder: t("epList_filterPlaceholder"),
        name: "episode-filter",
    });
    filterInput.setAttribute("aria-label", t("epList_filterAriaLabel"));
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

    function ticksToMinutes(ticks: number | null): string {
        if (!ticks) return "";
        const minutes = Math.round(ticks / 10_000_000 / 60);
        return minutes + "\u00A0min";
    }

    let isMovieView = false;

    function buildCard(
        ep: EpisodeItem,
        result: ApiResult<TimestampMap> | null,
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
        info.append(header);

        if (!result || !result.ok) {
            card.classList.add("error");
            const errorDiv = el("div", { className: "ts-episode-error" });
            errorDiv.append(document.createTextNode(t("epList_failedToLoadTimestamps")));

            const retryBtn = el("button", { className: "ts-retry-link", type: "button" }, t("epList_retry"));
            retryBtn.setAttribute("aria-label", t("epList_retryAriaLabel", { name: ep.Name }));
            retryBtn.addEventListener("click", async () => {
                // Retry only this episode so one failed request does not force a full reload.
                retryBtn.textContent = t("epList_loading");
                retryBtn.style.pointerEvents = "none";
                const retryResult = await api.getEpisodeTimestamps(ep.Id);
                if (retryResult && retryResult.ok) {
                    card.classList.remove("error");
                    info.removeChild(errorDiv);
                    info.append(buildTimestampPills(retryResult.data ?? {}));
                } else {
                    retryBtn.textContent = t("epList_retry");
                    retryBtn.style.pointerEvents = "";
                }
            });
            errorDiv.append(retryBtn);
            info.append(errorDiv);
        } else {
            info.append(buildTimestampPills(result.data ?? {}));
        }

        card.append(info);
        return card;
    }

    function buildTimestampPills(ts: Record<string, { Start: number; End: number }>): HTMLElement {
        const row = el("div", { className: "ts-episode-timestamps" });
        for (const mode of TIMESTAMP_MODES) {
            const seg = ts[mode.key];
            if (seg && (seg.Start !== 0 || seg.End !== 0)) {
                row.append(
                    el(
                        "span",
                        { className: "ts-timestamp-pill" },
                        mode.label + " " + formatTime(seg.Start) + " \u2013 " + formatTime(seg.End),
                    ),
                );
            } else {
                row.append(
                    el("span", { className: "ts-timestamp-missing" }, mode.label + " \u2013"),
                );
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
            countEl.textContent = t("epList_noMatchingEpisodes");
            return;
        }

        countEl.textContent =
            visibleCount === 1
                ? t("epList_oneEpisode")
                : t("epList_manyEpisodes", { count: visibleCount });
    }

    const handleFilterInput = () => {
        if (filterTimer) clearTimeout(filterTimer);
        filterTimer = setTimeout(() => {
            applyFilter();
        }, FILTER_DEBOUNCE_MS);
    };

    filterInput.addEventListener("input", handleFilterInput);

    return {
        container,

        render(
            episodes: EpisodeItem[],
            timestamps: Array<ApiResult<TimestampMap> | null>,
            isMovie = false,
        ) {
            isMovieView = isMovie;
            currentEpisodes = episodes;
            listEl.replaceChildren();
            currentCards = [];
            if (filterTimer) clearTimeout(filterTimer);
            filterInput.value = "";

            if (episodes.length === 0) {
                listEl.append(el("div", { className: "ts-status-msg" }, t("epList_noEpisodesFound")));
                countEl.textContent = "";
                return;
            }

            for (let i = 0; i < episodes.length; i++) {
                const card = buildCard(episodes[i], timestamps[i] ?? null, i);
                currentCards.push(card);
                listEl.append(card);
            }

            countEl.textContent =
                episodes.length === 1
                    ? t("epList_oneEpisode")
                    : t("epList_manyEpisodes", { count: episodes.length });
            statusEl.style.display = "none";
        },

        clear() {
            listEl.replaceChildren();
            currentCards = [];
            currentEpisodes = [];
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
            filterInput.removeEventListener("input", handleFilterInput);
        },
    };
}
