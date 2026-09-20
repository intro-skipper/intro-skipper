import { configSchema, type KeyOfKind } from "../config/schema.ts";
import { configStore } from "../store/config-store.ts";
import { formFor } from "./input-field.ts";
import { exclusionListField, type ExclusionListBehaviour } from "./exclusion-list-field.ts";
import { inlineCheckboxGroup } from "./inline-checkbox-group.ts";

/**
 * The config-bound controls of one tab render, all over the plugin config
 * store. Every store subscription and listener they open ends when `signal`
 * aborts, so a tab never cleans up by hand.
 */
export function configForm(signal: AbortSignal) {
    const form = formFor(configSchema, configStore, signal);
    return {
        field: form.field,
        list(id: KeyOfKind<"list">, behaviour: ExclusionListBehaviour = {}): HTMLElement {
            return exclusionListField(id, { ...behaviour, signal });
        },
        checkboxGroup(title: string, ids: readonly KeyOfKind<"checkbox">[]): HTMLElement {
            return inlineCheckboxGroup(title, ids, signal);
        },
        /** Shows `element` only while `visible()` holds, re-checked on every config change. */
        visibleWhen(element: HTMLElement, visible: () => boolean): void {
            const evalVisibility = () => {
                element.style.display = visible() ? "" : "none";
            };
            configStore.subscribe("loaded", evalVisibility, { signal });
            configStore.subscribe("changed", evalVisibility, { signal });
            if (configStore.isLoaded()) {
                evalVisibility();
            }
        },
    };
}

export type ConfigForm = ReturnType<typeof configForm>;
