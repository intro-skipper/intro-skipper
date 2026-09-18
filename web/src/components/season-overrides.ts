import type { FieldSchema, FieldStore, FieldStoreEvents, ValuesOf } from "../config/field-spec.ts";
import { configSchema } from "../config/schema.ts";
import { validateSpec } from "../config/validate.ts";
import { eventBus } from "../lifecycle.ts";
import { configStore } from "../store/config-store.ts";

// What the global setting currently is, shown where a blank override falls back to it.
function globalValue(key: "AnalysisPercent" | "AnalysisLengthLimit"): () => string {
    return () =>
        configStore.isLoaded() ? "Global: " + String(configStore.get(key)) : "Global default";
}

/**
 * The per-season analysis overrides the action bar edits. Same limits as the
 * global fields, so an override can never be a value the global setting would
 * reject; null means "inherit the global setting".
 */
export const overrideSchema = {
    AnalysisPercent: {
        kind: "number",
        label: "Percent of media to analyze",
        description: "Percentage of each item's runtime.",
        min: configSchema.AnalysisPercent.min,
        max: configSchema.AnalysisPercent.max,
        step: 1,
        optional: true,
        placeholder: globalValue("AnalysisPercent"),
    },
    AnalysisLengthLimit: {
        kind: "number",
        label: "Maximum runtime to analyze (minutes)",
        description: "Upper limit for each item.",
        min: configSchema.AnalysisLengthLimit.min,
        step: 1,
        optional: true,
        placeholder: globalValue("AnalysisLengthLimit"),
    },
    PreviewFromCreditsEnd: {
        kind: "select",
        label: "Set after credits scene as preview",
        description:
            "Creates a preview from the end of credits to the next credits block or episode end.",
        options: [
            {
                value: null,
                label: () =>
                    configStore.isLoaded()
                        ? configStore.get("AnimePreviewFromCreditsEnd")
                            ? "Global: Anime only"
                            : "Global: Disabled"
                        : "Global default",
            },
            { value: true, label: "Enabled" },
            { value: false, label: "Disabled" },
        ],
    },
} as const satisfies FieldSchema;

export type SeasonOverrides = ValuesOf<typeof overrideSchema>;
type OverrideKey = keyof SeasonOverrides;

export const overrideKeys = Object.keys(overrideSchema) as OverrideKey[];

const EMPTY: SeasonOverrides = {
    AnalysisPercent: null,
    AnalysisLengthLimit: null,
    PreviewFromCreditsEnd: null,
};

export type OverrideStore = FieldStore<SeasonOverrides> & {
    /** Replaces every value with the season's saved overrides and marks the store loaded. */
    load(values: SeasonOverrides): void;
    values(): SeasonOverrides;
    /** The message of the first field that fails its rules, or null when all pass. */
    firstError(): string | null;
};

/** The second field store: one season's overrides, edited by the action bar. */
export function createOverrideStore(): OverrideStore {
    let values: SeasonOverrides = { ...EMPTY };
    let loaded = false;
    const errors = new Map<OverrideKey, string>();
    const bus = eventBus<FieldStoreEvents<OverrideKey>>();

    return {
        isLoaded: () => loaded,
        get: (field) => values[field],
        set<K extends OverrideKey>(field: K, value: SeasonOverrides[K]): void {
            values[field] = value;
            const error = validateSpec(overrideSchema[field], value);
            if (error) errors.set(field, error);
            else errors.delete(field);
            bus.emit("changed", { field });
            bus.emit("validation", { field, error });
        },
        subscribe: bus.on,
        load(next) {
            values = { ...next };
            errors.clear();
            loaded = true;
            bus.emit("loaded");
        },
        values: () => ({ ...values }),
        firstError: () => errors.values().next().value ?? null,
    };
}
