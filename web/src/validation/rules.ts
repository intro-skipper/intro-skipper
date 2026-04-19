import type { PluginConfig } from "../types.ts";

// Small validation helpers shared by the config store and form fields.
export type ValidationRule<T> = (value: T) => string | null;

// Rule factories.
export function range(min: number, max: number): ValidationRule<number> {
    return (value) => (value < min || value > max ? `Must be between ${min} and ${max}` : null);
}

export function minValue(min: number): ValidationRule<number> {
    return (value) => (value < min ? `Must be at least ${min}` : null);
}

export function validRegex(): ValidationRule<string> {
    return (value) => {
        if (!value || value.trim().length === 0) return null; // empty is OK — falls back to default
        try {
            new RegExp(value);
            return null;
        } catch {
            return "Invalid regular expression";
        }
    };
}

// Per-field validation rules.
export const validationRules: Partial<Record<keyof PluginConfig, ValidationRule<any>[]>> = {
    AnalysisPercent: [range(1, 90)],
    AnalysisLengthLimit: [minValue(1)],
    MinimumIntroDuration: [minValue(1)],
    MaximumIntroDuration: [minValue(1)],
    MinimumCreditsDuration: [minValue(1)],
    MaximumCreditsDuration: [minValue(1)],
    MaximumMovieCreditsDuration: [minValue(1)],
    BlackFrameMinimumPercentage: [range(0, 100)],
    BlackFrameThreshold: [range(16, 255)],
    MaxParallelism: [minValue(1)],
    ProcessThreads: [range(0, 16)],
    SkipbuttonHideDelay: [range(0, 1000)],
    SilenceDetectionMaximumNoise: [range(-90, 0)],
    SilenceDetectionMinimumDuration: [minValue(0)],
    ChapterAnalyzerIntroductionPattern: [validRegex()],
    ChapterAnalyzerEndCreditsPattern: [validRegex()],
    ChapterAnalyzerPreviewPattern: [validRegex()],
    ChapterAnalyzerRecapPattern: [validRegex()],
    ChapterAnalyzerCommercialPattern: [validRegex()],
};

// Min/max field pairs that must stay ordered.
export const CROSS_FIELD_PAIRS: Array<[keyof PluginConfig, keyof PluginConfig]> = [
    ["MinimumIntroDuration", "MaximumIntroDuration"],
    ["MinimumCreditsDuration", "MaximumCreditsDuration"],
    ["MinimumRecapDuration", "MaximumRecapDuration"],
    ["MinimumPreviewDuration", "MaximumPreviewDuration"],
    ["MinimumCommercialDuration", "MaximumCommercialDuration"],
];
