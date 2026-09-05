import type { PluginConfig } from "../types.ts";
import { withDashboardLoading } from "../components/async-feedback.ts";
import { loadPluginConfig, savePluginConfig, updateSkipDuration } from "./api.ts";
import { validator } from "../validation/validator.ts";

// Central config store for the dashboard. Keeps the loaded config, tracks
// dirty state, and emits validation updates for bound fields.
let config: PluginConfig | null = null;
let snapshot: PluginConfig | null = null;


// Event name to listener argument tuple.
type StoreEvents = {
    loaded: [];
    saved: [];
    changed: [{ field: keyof PluginConfig }];
    validation: [{ field: keyof PluginConfig; error: string | null }];
};

type Listener<K extends keyof StoreEvents> = (...args: StoreEvents[K]) => void;

const listeners: { [K in keyof StoreEvents]: Set<Listener<K>> } = {
    loaded: new Set(),
    saved: new Set(),
    changed: new Set(),
    validation: new Set(),
};

// Track subscriptions created while a tab renders so they can be removed
// together when the tab is torn down.
let scopedUnsubscribes: Array<() => void> = [];
let trackingScope = false;

function emit<K extends keyof StoreEvents>(event: K, ...args: StoreEvents[K]): void {
    for (const cb of listeners[event]) {
        cb(...args);
    }
}

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
    loadedConfig.SeriesExclusions = normalizeStringList(loadedConfig.SeriesExclusions);
    loadedConfig.MovieExclusions = normalizeStringList(loadedConfig.MovieExclusions);
    loadedConfig.PathExclusions = normalizeStringList(loadedConfig.PathExclusions);
    return loadedConfig;
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
    subscribe<K extends keyof StoreEvents>(event: K, callback: Listener<K>): void {
        listeners[event].add(callback);
        if (trackingScope) {
            scopedUnsubscribes.push(() => listeners[event].delete(callback));
        }
    },

    unsubscribe<K extends keyof StoreEvents>(event: K, callback: Listener<K>): void {
        listeners[event].delete(callback);
    },

    /** Start tracking subscriptions. Call before rendering a tab. */
    beginScope(): void {
        trackingScope = true;
        scopedUnsubscribes = [];
    },

    /** Remove all subscriptions added since beginScope(). Call on tab destroy. */
    endScope(): void {
        for (const unsubscribe of scopedUnsubscribes) {
            unsubscribe();
        }
        scopedUnsubscribes = [];
        trackingScope = false;
    },

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

    set<K extends keyof PluginConfig>(field: K, value: PluginConfig[K]): void {
        if (!config || !snapshot) throw new Error("Config not loaded");

        // The store owns updates, so it can write through the readonly type here.
        (config as unknown as Record<string, PluginConfig[keyof PluginConfig]>)[field as string] =
            value;

        // Run direct validation first.
        let error = validator.validate(field, value);

        // Only run paired min/max checks if the field passed its own rules.
        if (!error) {
            error = validator.validateCrossFieldFor(field, config);
        }

        // Re-check the matching field in each min/max pair so both inputs stay in sync.
        const linkedFields = validator.getLinkedFields(field);
        for (const linked of linkedFields) {
            let linkedError = validator.validate(linked, config[linked]);
            if (!linkedError) {
                linkedError = validator.validateCrossFieldFor(linked, config);
            }
            emit("validation", { field: linked, error: linkedError });
        }

        emit("changed", { field });
        emit("validation", { field, error });
    },

    async save(): Promise<void> {
        await withDashboardLoading(async () => {
            const serverConfig = normalizePluginConfig(await loadPluginConfig());
            Object.assign(serverConfig, config);
            const result = await savePluginConfig(serverConfig);

            // Keep the skip-button patch in sync, but do not block saving on it.
            updateSkipDuration().catch(console.error);

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
