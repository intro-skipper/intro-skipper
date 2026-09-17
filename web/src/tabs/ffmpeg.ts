import type { Tab } from "../types.ts";
import { configForm } from "../components/config-form.ts";

export const ffmpegTab: Tab = {
    id: "ffmpeg",
    label: "FFmpeg",
    render(container, signal) {
        const form = configForm(signal);

        container.append(
            form.field("MaxParallelism"),
            form.field("ProcessPriority"),
            form.field("ProcessThreads"),
            form.field("ScanTimeoutSeconds"),
            form.field("ProbeAudioDuration"),
            form.field("PreferredAudioLanguage"),
            form.field("PreferAudioStreamWithMostChannels"),
            form.field("CacheCompressionLevel"),
        );
    },
};
