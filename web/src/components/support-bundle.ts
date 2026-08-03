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
        (fields.get("Warnings") ?? "")
            .split(",")
            .map((warning) => warning.trim())
            .filter(Boolean),
    );

    return {
        warnings,
        ffmpegStatus: fields.get("FFmpeg") ?? null,
    };
}

export function isChromaprintUnavailable(bundle: SupportBundleInfo): boolean {
    return (
        bundle.warnings.has("IncompatibleFFmpegBuild") &&
        bundle.ffmpegStatus === "chromaprint_not_supported"
    );
}
