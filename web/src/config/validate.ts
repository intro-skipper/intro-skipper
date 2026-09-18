import type { FieldSpec } from "./field-spec.ts";
import {
    configKeys,
    configSchema,
    orderedPairs,
    type ConfigKey,
    type ConfigKeysOfType,
    type PluginConfig,
} from "./schema.ts";

type NumberKey = ConfigKeysOfType<number>;

/**
 * Range and format rules for one value, read from its field spec. Works for any
 * schema; pair ordering is validatePair's job. Returns the message to show, or null.
 */
export function validateSpec(spec: FieldSpec, value: unknown): string | null {
    switch (spec.kind) {
        case "number": {
            // Null is an optional field left empty; anything else non-numeric is
            // still being typed.
            if (typeof value !== "number") return null;
            const { min, max, step } = spec;
            if (step !== undefined && Number.isInteger(step) && !Number.isInteger(value)) {
                return "Must be a whole number";
            }
            if (min !== undefined && max !== undefined && (value < min || value > max)) {
                return `Must be between ${min} and ${max}`;
            }
            if (min !== undefined && value < min) return `Must be at least ${min}`;
            if (max !== undefined && value > max) return `Must be at most ${max}`;
            return null;
        }
        case "regex": {
            // Empty means "use the default pattern".
            if (typeof value !== "string" || value.trim().length === 0) return null;
            try {
                new RegExp(value);
                return null;
            } catch {
                return "Invalid regular expression";
            }
        }
        default:
            return null;
    }
}

/** The other side of the min/max pair this config field belongs to, if any. */
export function linkedField(key: ConfigKey): NumberKey | null {
    for (const [minKey, maxKey] of orderedPairs) {
        if (key === minKey) return maxKey;
        if (key === maxKey) return minKey;
    }
    return null;
}

/**
 * Whether this config field still respects its min/max pair. Callers run it
 * only after validateSpec passes, so a field never shows two messages.
 */
export function validatePair(key: ConfigKey, config: PluginConfig): string | null {
    for (const [minKey, maxKey] of orderedPairs) {
        if (key === minKey) {
            return config[minKey] >= config[maxKey] ? "Must be less than maximum" : null;
        }
        if (key === maxKey) {
            return config[minKey] >= config[maxKey] ? "Must be greater than minimum" : null;
        }
    }
    return null;
}

/** Every current config error, keyed by field. Pair errors only when both sides pass alone. */
export function validateAll(config: PluginConfig): Map<ConfigKey, string> {
    const errors = new Map<ConfigKey, string>();

    for (const key of configKeys) {
        const error = validateSpec(configSchema[key], config[key]);
        if (error) errors.set(key, error);
    }

    for (const [minKey, maxKey] of orderedPairs) {
        if (errors.has(minKey) || errors.has(maxKey)) continue;
        if (config[minKey] >= config[maxKey]) {
            errors.set(minKey, "Must be less than maximum");
            errors.set(maxKey, "Must be greater than minimum");
        }
    }

    return errors;
}
