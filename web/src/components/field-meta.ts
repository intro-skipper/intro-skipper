import { htmlEl } from "./dom.ts";

type FieldMetaOptions = {
    idBase?: string;
    description?: string;
    warning?: string;
};

/** Appends optional description and warning elements to a field container. */
export function appendFieldMeta(container: HTMLElement, opts: FieldMetaOptions): string[] {
    const describedByIds: string[] = [];

    if (opts.description) {
        const descAttrs: Record<string, string> = { className: "field-description" };
        if (opts.idBase) {
            descAttrs.id = opts.idBase + "-description";
        }

        const desc = htmlEl("div", descAttrs, opts.description);
        container.append(desc);
        if (desc.id) {
            describedByIds.push(desc.id);
        }
    }

    if (opts.warning) {
        const warnAttrs: Record<string, string> = { className: "field-warning" };
        if (opts.idBase) {
            warnAttrs.id = opts.idBase + "-warning";
        }

        const warn = htmlEl("div", warnAttrs, opts.warning);
        container.append(warn);
        if (warn.id) {
            describedByIds.push(warn.id);
        }
    }

    return describedByIds;
}
