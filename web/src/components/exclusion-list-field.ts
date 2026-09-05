import { configStore, trimmedEntries } from "../store/config-store.ts";
import { el } from "./dom.ts";
import { setDescribedBy } from "./field-bind.ts";
import { appendFieldMeta } from "./field-meta.ts";

type ExclusionListFieldId = "SeriesExclusions" | "MovieExclusions" | "PathExclusions";

type ExclusionListFieldOptions = {
    id: ExclusionListFieldId;
    label: string;
    description?: string;
    placeholder?: string;
    suggestions?: () => Promise<string[]>;
    confirmAdd?: (value: string) => Promise<boolean>;
};

function hasEntry(entries: string[], value: string): boolean {
    return entries.some((entry) => entry.toLocaleLowerCase() === value.toLocaleLowerCase());
}

function readValues(field: ExclusionListFieldId): string[] {
    return trimmedEntries(configStore.get(field));
}

export function exclusionListField(opts: ExclusionListFieldOptions): HTMLElement {
    const container = el("div", { className: "input-container exclusion-list-field" });
    const inputId = "field-" + opts.id;
    const suggestionsId = inputId + "-suggestions";

    const label = el("label", { className: "input-label", for: inputId }, opts.label);
    const input = document.createElement("input");
    input.type = "text";
    input.id = inputId;
    input.name = opts.id;
    input.autocomplete = "off";
    if (opts.placeholder) {
        input.placeholder = opts.placeholder;
    }

    const addButton = el(
        "button",
        { className: "exclusion-add-button", type: "button" },
        "Add",
    );
    const inputRow = el("div", { className: "exclusion-input-row" });
    inputRow.append(input, addButton);

    const errorDiv = el("div", {
        className: "field-error",
        id: inputId + "-error",
        role: "status",
        "aria-live": "polite",
        "aria-atomic": "true",
    });
    errorDiv.style.display = "none";

    const list = el("ul", { className: "exclusion-list-values" });
    const empty = el("div", { className: "exclusion-list-empty" }, "No entries");
    const describedByIds = [errorDiv.id];

    container.append(label, inputRow, errorDiv);
    describedByIds.push(...appendFieldMeta(container, { ...opts, idBase: inputId }));
    container.append(list, empty);
    setDescribedBy(input, describedByIds);

    function showError(message: string | null): void {
        errorDiv.textContent = message ?? "";
        errorDiv.style.display = message ? "" : "none";
        input.classList.toggle("field-error-active", Boolean(message));
        if (message) {
            input.setAttribute("aria-invalid", "true");
        } else {
            input.removeAttribute("aria-invalid");
        }
    }

    function render(): void {
        const values = readValues(opts.id);
        list.replaceChildren();
        empty.style.display = values.length === 0 ? "" : "none";

        for (const value of values) {
            const item = el("li", { className: "exclusion-list-item" });
            const text = el("span", { className: "exclusion-list-value" }, value);
            const removeButton = el(
                "button",
                {
                    className: "exclusion-remove-button",
                    type: "button",
                    "aria-label": "Remove " + value,
                },
                "x",
            );
            removeButton.addEventListener("click", () => {
                configStore.set(
                    opts.id,
                    readValues(opts.id).filter((entry) => entry !== value),
                );
                showError(null);
            });
            item.append(text, removeButton);
            list.append(item);
        }
    }

    async function addCurrentValue(): Promise<void> {
        const value = input.value.trim();
        if (value.length === 0) {
            showError("Enter a value before adding it.");
            return;
        }

        const values = readValues(opts.id);
        if (hasEntry(values, value)) {
            showError("This entry is already listed.");
            return;
        }

        if (opts.confirmAdd && !(await opts.confirmAdd(value))) {
            return;
        }

        configStore.set(opts.id, [...values, value]);
        input.value = "";
        showError(null);
        input.focus();
    }

    function handleAdd(): void {
        addCurrentValue().catch((error: unknown) => {
            console.error("Failed to add exclusion entry", error);
            showError("Failed to add entry.");
        });
    }

    addButton.addEventListener("click", handleAdd);
    input.addEventListener("keydown", (event) => {
        if (event.key === "Enter") {
            event.preventDefault();
            handleAdd();
        }
    });

    configStore.subscribe("loaded", render);
    configStore.subscribe("changed", ({ field }) => {
        if (field === opts.id) {
            render();
        }
    });

    if (configStore.isLoaded()) {
        render();
    }

    // Suggestions are fetched on first focus so rendering the tab costs no requests.
    const suggestions = opts.suggestions;
    if (suggestions) {
        const datalist = el("datalist", { id: suggestionsId });
        input.setAttribute("list", suggestionsId);
        container.append(datalist);

        input.addEventListener(
            "focus",
            () => {
                suggestions()
                    .then((values) => {
                        const seen = new Set<string>();
                        for (const value of trimmedEntries(values)) {
                            const key = value.toLocaleLowerCase();
                            if (seen.has(key)) {
                                continue;
                            }

                            seen.add(key);
                            datalist.append(el("option", { value }));
                        }
                    })
                    .catch((error: unknown) => {
                        console.error("Failed to load exclusion suggestions", error);
                    });
            },
            { once: true },
        );
    }

    return container;
}
