/** Support-bundle field holding the comma-separated plugin warning flags. */
const WARNINGS_FIELD = "Warnings";

/** Support-bundle field holding the FFmpeg feature-detection status. */
const FFMPEG_FIELD = "FFmpeg";

/** Warning flag raised by the server when the FFmpeg build lacks required features. */
const INCOMPATIBLE_FFMPEG_BUILD_WARNING = "IncompatibleFFmpegBuild";

/** FFmpeg status reported when the build has no chromaprint (fingerprinting) support. */
const CHROMAPRINT_NOT_SUPPORTED_STATUS = "chromaprint_not_supported";

/** Parsed status fields from the Markdown support bundle. */
export interface SupportBundleInfo {
    warnings: ReadonlySet<string>;
    ffmpegStatus: string | null;
}

const STATUS_FIELD_PATTERN = /^\s*\*\s+([^:]+):\s*`([^`]*)`\s*$/gm;

/** Parse the structured status fields emitted by the support-bundle endpoint. */
export function parseSupportBundle(bundle: string): SupportBundleInfo {
    const fields = new Map<string, string>();
    for (const match of bundle.matchAll(STATUS_FIELD_PATTERN)) {
        const fieldName = match[1]?.trim();
        const fieldValue = match[2]?.trim();
        if (fieldName && fieldValue !== undefined) {
            fields.set(fieldName, fieldValue);
        }
    }

    const warnings = new Set(
        (fields.get(WARNINGS_FIELD) ?? "")
            .split(",")
            .map((warning) => warning.trim())
            .filter(Boolean),
    );

    return {
        warnings,
        ffmpegStatus: fields.get(FFMPEG_FIELD) ?? null,
    };
}

export function isChromaprintUnavailable(bundle: SupportBundleInfo): boolean {
    return (
        bundle.warnings.has(INCOMPATIBLE_FFMPEG_BUILD_WARNING) &&
        bundle.ffmpegStatus === CHROMAPRINT_NOT_SUPPORTED_STATUS
    );
}
