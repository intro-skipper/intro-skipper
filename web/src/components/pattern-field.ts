import { configSchema, type KeyOfKind } from "../config/schema.ts";
import { configStore } from "../store/config-store.ts";
import { el } from "./dom.ts";
import type { ConfigForm } from "./config-form.ts";

/** A regex field with a button that writes the schema default back. */
export function patternField(form: ConfigForm, id: KeyOfKind<"regex">): HTMLElement {
    const wrapper = el("div", { className: "pattern-field" });
    const resetBtn = el(
        "button",
        { className: "action-button reset-button", type: "button" },
        "Reset to default",
    );
    resetBtn.addEventListener("click", () => {
        configStore.set(id, configSchema[id].default);
    });
    wrapper.append(form.field(id), resetBtn);
    return wrapper;
}
