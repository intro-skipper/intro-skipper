import type { Tab } from "../types.ts";
import { configField } from "../components/input-field.ts";

export const ffmpegTab: Tab = {
    id: "ffmpeg",
    label: "FFmpeg",
    render(container) {
        container.append(
            configField("MaxParallelism"),
            configField("ProcessPriority"),
            configField("ProcessThreads"),
            configField("ScanTimeoutSeconds"),
            configField("ProbeAudioDuration"),
            configField("PreferredAudioLanguage"),
            configField("PreferAudioStreamWithMostChannels"),
            configField("CacheCompressionLevel"),
        );
    },
};
