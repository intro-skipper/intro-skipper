import type { Tab } from "../types.ts";
import { numberField } from "../components/number-field.ts";
import { selectField } from "../components/select-field.ts";
import { appendTabContent } from "../components/tab-layout.ts";
import { t } from "../i18n/index.ts";

export const ffmpegTab: Tab = {
    id: "ffmpeg",
    label: () => t("tab_ffmpeg"),
    render(container) {
        appendTabContent(
            container,
            numberField({
                id: "MaxParallelism",
                label: t("ffmpeg_maxParallelismLabel"),
                min: 1,
                description: t("ffmpeg_maxParallelismDesc"),
            }),
            selectField({
                id: "ProcessPriority",
                label: t("ffmpeg_priorityLabel"),
                options: [
                    { value: "Idle", label: t("ffmpeg_priorityIdle") },
                    { value: "BelowNormal", label: t("ffmpeg_priorityBelowNormal") },
                    { value: "Normal", label: t("ffmpeg_priorityNormal") },
                    { value: "AboveNormal", label: t("ffmpeg_priorityAboveNormal") },
                    { value: "High", label: t("ffmpeg_priorityHigh") },
                    { value: "RealTime", label: t("ffmpeg_priorityHighest") },
                ],
                description: t("ffmpeg_priorityDesc"),
            }),
            numberField({
                id: "ProcessThreads",
                label: t("ffmpeg_threadsLabel"),
                min: 0,
                max: 16,
                description: t("ffmpeg_threadsDesc"),
            }),
            selectField({
                id: "CacheCompressionLevel",
                label: t("ffmpeg_cacheLabel"),
                options: [
                    { value: "NoCompression", label: t("ffmpeg_cacheNoCompression") },
                    { value: "Fastest", label: t("ffmpeg_cacheFastest") },
                    { value: "Optimal", label: t("ffmpeg_cacheOptimal") },
                    { value: "SmallestSize", label: t("ffmpeg_cacheSmallestSize") },
                ],
                description: t("ffmpeg_cacheDesc"),
            }),
        );
    },
};
