import type { FieldStoreEvents } from "../config/field-spec.ts";
import { configKeys, configSchema, isKind, type ConfigKey, type PluginConfig } from "../config/schema.ts";
import { linkedField, validatePair, validateSpec } from "../config/validate.ts";
import { withDashboardLoading } from "../components/async-feedback.ts";
import { eventBus } from "../lifecycle.ts";
import { loadPluginConfig, savePluginConfig, updateSkipDuration } from "./api.ts";

// Central config store for the dashboard. Keeps the loaded config, tracks
// dirty state, and emits validation updates for bound fields.
let config: PluginConfig | null = null;
let snapshot: PluginConfig | null = null;

const bus = eventBus<FieldStoreEvents<ConfigKey> & { saved: [] }>();
const { emit } = bus;

// The one normalizer for exclusion entries, applied on load and by the field on
// write, so a stale untrimmed or empty entry from the server cannot keep a field
// dirty after the user touches it.
export function trimmedEntries(values: readonly string[]): string[] {
    return values.map((value) => value.trim()).filter((value) => value.length > 0);
}

function normalizeStringList(value: unknown): string[] {
    return Array.isArray(value)
        ? trimmedEntries(value.filter((item): item is string => typeof item === "string"))
        : [];
}

function normalizePluginConfig(loadedConfig: PluginConfig): PluginConfig {
    for (const key of configKeys) {
        if (isKind(key, "list")) {
            loadedConfig[key] = normalizeStringList(loadedConfig[key]);
        }
    }
    return loadedConfig;
}

// Direct rules first; the pair check only once the field passes on its own.
function fieldError(field: ConfigKey, current: PluginConfig): string | null {
    return validateSpec(configSchema[field], current[field]) ?? validatePair(field, current);
}

// Config values are primitives or string arrays, so a shallow element compare
// is a full equality check.
function sameValue(a: PluginConfig[keyof PluginConfig], b: PluginConfig[keyof PluginConfig]): boolean {
    if (Array.isArray(a) && Array.isArray(b)) {
        return a.length === b.length && a.every((item, index) => item === b[index]);
    }
    return a === b;
}

function takeSnapshot(source: PluginConfig): void {
    snapshot = JSON.parse(JSON.stringify(source)) as PluginConfig;
}

export const configStore = {
    /** Listens until `signal` aborts. Every subscriber belongs to a mounted view. */
    subscribe: bus.on,

    async load(): Promise<void> {
        try {
            config = normalizePluginConfig(await loadPluginConfig());
            takeSnapshot(config);
            emit("loaded");
        } catch (err) {
            console.error("Failed to load plugin configuration", err);
            window.Dashboard.alert("Failed to load configuration");
            throw new Error("Failed to load plugin configuration");
        }
    },

    get<K extends keyof PluginConfig>(field: K): PluginConfig[K] {
        if (!config) throw new Error("Config not loaded");
        return config[field];
    },

    getAll(): PluginConfig {
        if (!config) throw new Error("Config not loaded");
        return config;
    },

    isLoaded(): boolean {
        return config !== null;
    },

    set<K extends ConfigKey>(field: K, value: PluginConfig[K]): void {
        if (!config || !snapshot) throw new Error("Config not loaded");

        config[field] = value;

        // Re-check the other half of a min/max pair so both inputs stay in sync.
        const linked = linkedField(field);
        if (linked) {
            emit("validation", { field: linked, error: fieldError(linked, config) });
        }

        emit("changed", { field });
        emit("validation", { field, error: fieldError(field, config) });
    },

    async save(): Promise<void> {
        await withDashboardLoading(async () => {
            const serverConfig = normalizePluginConfig(await loadPluginConfig());
            Object.assign(serverConfig, config);
            const result = await savePluginConfig(serverConfig);

            // Keep the skip-button patch in sync, but do not block saving on it.
            void updateSkipDuration().then((result) => {
                if (!result.ok) console.error("Failed to update skip duration", result.error);
            });

            config = serverConfig;
            takeSnapshot(serverConfig);
            window.Dashboard.processPluginConfigurationUpdateResult(result);
            emit("saved");
        });
    },

    isDirty(): boolean {
        if (!config || !snapshot) return false;
        const current = config;
        const saved = snapshot;
        return (Object.keys(saved) as (keyof PluginConfig)[]).some(
            (field) => !sameValue(current[field], saved[field]),
        );
    },
};
