import type { Tab } from "../types.ts";
import { numberField } from "../components/number-field.ts";
import { selectField } from "../components/select-field.ts";
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
        );
    },
};
