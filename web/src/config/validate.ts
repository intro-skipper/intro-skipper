import {
    configKeys,
    configSchema,
    orderedPairs,
    type ConfigKey,
    type ConfigKeysOfType,
    type FieldSpec,
    type PluginConfig,
} from "./schema.ts";

type NumberKey = ConfigKeysOfType<number>;

/**
 * Range and format rules for one field, read from its schema entry. Pair
 * ordering is validatePair's job. Returns the message to show, or null.
 */
export function validateField(key: ConfigKey, value: PluginConfig[ConfigKey]): string | null {
    const spec: FieldSpec = configSchema[key];
    switch (spec.kind) {
        case "number": {
            if (typeof value !== "number") return null;
            const { min, max } = spec;
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

/** The other side of the min/max pair this field belongs to, if any. */
export function linkedField(key: ConfigKey): NumberKey | null {
    for (const [minKey, maxKey] of orderedPairs) {
        if (key === minKey) return maxKey;
        if (key === maxKey) return minKey;
    }
    return null;
}

/**
 * Whether this field still respects its min/max pair. Callers run it only
 * after validateField passes, so a field never shows two messages.
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

/** Every current error, keyed by field. Pair errors only when both sides pass alone. */
export function validateAll(config: PluginConfig): Map<ConfigKey, string> {
    const errors = new Map<ConfigKey, string>();

    for (const key of configKeys) {
        const error = validateField(key, config[key]);
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
