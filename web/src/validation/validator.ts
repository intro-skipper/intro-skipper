import type { PluginConfig } from "../types.ts";
import { validationRules, CROSS_FIELD_PAIRS } from "./rules.ts";

// Runs field-level and paired min/max validation for the config store.
class Validator {
    /**
     * Run every rule for one field and return the first error.
     */
    validate(field: keyof PluginConfig, value: unknown): string | null {
        const rules = validationRules[field];
        if (!rules) return null;

        for (const rule of rules) {
            const error = rule(value as never);
            if (error) return error;
        }
        return null;
    }

    /**
     * Return the other side of each min/max pair that includes this field.
     */
    getLinkedFields(field: keyof PluginConfig): Array<keyof PluginConfig> {
        const linked: Array<keyof PluginConfig> = [];
        for (const [a, b] of CROSS_FIELD_PAIRS) {
            if (field === a) linked.push(b);
            else if (field === b) linked.push(a);
        }
        return linked;
    }

    /**
     * Check whether this field still satisfies its min/max pair.
     */
    validateCrossFieldFor(field: keyof PluginConfig, config: PluginConfig): string | null {
        for (const [minField, maxField] of CROSS_FIELD_PAIRS) {
            if (field === minField) {
                const minVal = config[minField] as number;
                const maxVal = config[maxField] as number;
                return minVal >= maxVal ? "Must be less than maximum" : null;
            }
            if (field === maxField) {
                const minVal = config[minField] as number;
                const maxVal = config[maxField] as number;
                return minVal >= maxVal ? "Must be greater than minimum" : null;
            }
        }
        return null;
    }

    /**
     * Validate every configured rule and return all current errors.
     */
    validateAll(config: PluginConfig): Map<keyof PluginConfig, string> {
        const errors = new Map<keyof PluginConfig, string>();

        for (const field of Object.keys(validationRules) as Array<keyof PluginConfig>) {
            const value = config[field];
            const error = this.validate(field, value);
            if (error) {
                errors.set(field, error);
            }
        }

        // Only surface pair errors when both individual fields already pass.
        for (const [minField, maxField] of CROSS_FIELD_PAIRS) {
            if (!errors.has(minField) && !errors.has(maxField)) {
                const minVal = config[minField] as number;
                const maxVal = config[maxField] as number;
                if (minVal >= maxVal) {
                    errors.set(minField, "Must be less than maximum");
                    errors.set(maxField, "Must be greater than minimum");
                }
            }
        }

        return errors;
    }
}

export const validator = new Validator();
