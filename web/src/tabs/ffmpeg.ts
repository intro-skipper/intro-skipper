import type { Tab } from "../types.ts";
import { checkboxField } from "../components/checkbox-field.ts";
import { numberField } from "../components/number-field.ts";
import { selectField } from "../components/select-field.ts";
import { textField } from "../components/text-field.ts";
import { appendTabContent } from "../components/tab-layout.ts";

export const ffmpegTab: Tab = {
    id: "ffmpeg",
    label: "FFmpeg",
    render(container) {
        appendTabContent(
            container,
            numberField({
                id: "MaxParallelism",
                label: "Maximum degree of parallelism",
                min: 1,
                description: "Maximum number of simultaneous async episode analysis operations.",
            }),
            selectField({
                id: "ProcessPriority",
                label: "FFmpeg Priority",
                options: [
                    { value: "Idle", label: "Idle" },
                    { value: "BelowNormal", label: "Below Normal" },
                    { value: "Normal", label: "Normal" },
                    { value: "AboveNormal", label: "Above Normal" },
                    { value: "High", label: "High" },
                    { value: "RealTime", label: "Highest" },
                ],
                description:
                    "Sets the relative priority of the analysis FFmpeg process to other parallel operations.",
            }),
            numberField({
                id: "ProcessThreads",
                label: "FFmpeg Threads",
                min: 0,
                max: 16,
                description:
                    "Number of simultaneous processes to use for FFmpeg operations. Setting 0 (default) uses the maximum threads available.",
            }),
            checkboxField({
                id: "ProbeAudioDuration",
                label: "Probe audio duration for credits",
                description:
                    "Use ffprobe to base credits fingerprinting on the first audio stream duration when container runtime is longer than the audio.",
            }),
            textField({
                id: "PreferredAudioLanguage",
                label: "Preferred stream audio language",
                placeholder: "eng",
                description:
                    "Prefer an audio stream with this language tag when generating Chromaprint fingerprints. Empty or unmatched values will use the stream selection policy below.",
            }),
            checkboxField({
                id: "PreferAudioStreamWithMostChannels",
                label: "Prefer audio channel count",
                description:
                    "When enabled, Chromaprint selects streams based on channel count, using the lowest index as the tie breaker. When disabled, the first stream is selected.",
            }),
            selectField({
                id: "CacheCompressionLevel",
                label: "Cache Compression Level",
                options: [
                    { value: "NoCompression", label: "No Compression" },
                    { value: "Fastest", label: "Fastest" },
                    { value: "Optimal", label: "Optimal" },
                    { value: "SmallestSize", label: "Smallest Size" },
                ],
                description:
                    "Controls the Brotli compression level for the detection cache. " +
                    "Higher compression reduces disk usage but increases CPU time during analysis. " +
                    "Changing this only affects newly cached data.",
            }),
        );
    },
};
