import type { KeyOfKind } from "../config/schema.ts";
import { configField, type ControlKey, type FieldRules } from "./input-field.ts";
import { exclusionListField, type ExclusionListBehaviour } from "./exclusion-list-field.ts";
import { inlineCheckboxGroup } from "./inline-checkbox-group.ts";
import { bindVisibility } from "./field-bind.ts";

/**
 * The config-bound controls of one tab render. Every store subscription and
 * listener they open ends when `signal` aborts, so a tab never cleans up by hand.
 */
export function configForm(signal: AbortSignal) {
    return {
        field(id: ControlKey, rules: FieldRules = {}): HTMLElement {
            return configField(id, { ...rules, signal });
        },
        list(id: KeyOfKind<"list">, behaviour: ExclusionListBehaviour = {}): HTMLElement {
            return exclusionListField(id, { ...behaviour, signal });
        },
        checkboxGroup(title: string, ids: readonly KeyOfKind<"checkbox">[]): HTMLElement {
            return inlineCheckboxGroup(title, ids, signal);
        },
        /** Shows `element` only while `visible()` holds, re-checked on every store change. */
        visibleWhen(element: HTMLElement, visible: () => boolean): void {
            bindVisibility(element, visible, signal);
        },
    };
}

export type ConfigForm = ReturnType<typeof configForm>;
