import type { PluginConfig, StoreEvent } from "../types.ts";
import { withDashboardLoading } from "../components/async-feedback.ts";
import { loadPluginConfig, savePluginConfig, updateSkipDuration } from "./api.ts";
import { validator } from "../validation/validator.ts";

// Central config store for the dashboard. Keeps the loaded config, tracks
// dirty state, and emits validation updates for bound fields.
let config: PluginConfig | null = null;
let snapshot: PluginConfig | null = null;
const listeners = new Map<StoreEvent, Set<(...args: unknown[]) => void>>();

// Track subscriptions created while a tab renders so they can be removed
// together when the tab is torn down.
let scopedListeners: Array<{ event: StoreEvent; callback: (...args: unknown[]) => void }> = [];
let trackingScope = false;

function normalizeStringList(value: unknown): string[] {
    return Array.isArray(value) ? value.filter((item) => typeof item === "string") : [];
}

function normalizePluginConfig(loadedConfig: PluginConfig): PluginConfig {
    loadedConfig.SeriesExclusions = normalizeStringList(loadedConfig.SeriesExclusions);
    loadedConfig.MovieExclusions = normalizeStringList(loadedConfig.MovieExclusions);
    loadedConfig.PathExclusions = normalizeStringList(loadedConfig.PathExclusions);
    return loadedConfig;
}

export const configStore = {
    subscribe(event: StoreEvent, callback: (...args: unknown[]) => void): void {
        if (!listeners.has(event)) {
            listeners.set(event, new Set());
        }
        listeners.get(event)!.add(callback);
        if (trackingScope) {
            scopedListeners.push({ event, callback });
        }
    },

    unsubscribe(event: StoreEvent, callback: (...args: unknown[]) => void): void {
        listeners.get(event)?.delete(callback);
    },

    /** Start tracking subscriptions. Call before rendering a tab. */
    beginScope(): void {
        trackingScope = true;
        scopedListeners = [];
    },

    /** Remove all subscriptions added since beginScope(). Call on tab destroy. */
    endScope(): void {
        for (const { event, callback } of scopedListeners) {
            listeners.get(event)?.delete(callback);
        }
        scopedListeners = [];
        trackingScope = false;
    },

    emit(event: StoreEvent, ...args: unknown[]): void {
        const set = listeners.get(event);
        if (set) {
            for (const cb of set) {
                cb(...args);
            }
        }
    },

    async load(): Promise<void> {
        try {
            config = normalizePluginConfig(await loadPluginConfig());
            snapshot = JSON.parse(JSON.stringify(config)) as PluginConfig;
            this.emit("loaded");
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
        if (!config) throw new Error("Config not loaded");

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
            this.emit("validation", { field: linked, error: linkedError });
        }

        this.emit("changed", { field, value });
        this.emit("validation", { field, error });
    },

    async save(): Promise<void> {
        await withDashboardLoading(async () => {
            const serverConfig = await loadPluginConfig();
            Object.assign(serverConfig, config);
            const result = await savePluginConfig(serverConfig);

            // Keep the skip-button patch in sync, but do not block saving on it.
            updateSkipDuration().catch(console.error);

            config = serverConfig;
            snapshot = JSON.parse(JSON.stringify(serverConfig)) as PluginConfig;
            window.Dashboard.processPluginConfigurationUpdateResult(result);
            this.emit("saved");
        });
    },

    isDirty(): boolean {
        if (!config || !snapshot) return false;
        return JSON.stringify(config) !== JSON.stringify(snapshot);
    },
};
