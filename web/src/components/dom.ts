export function el<K extends keyof HTMLElementTagNameMap>(
    tag: K,
    attrs?: Record<string, string>,
    ...children: Array<string | Node>
): HTMLElementTagNameMap[K] {
    const element = document.createElement(tag);
    if (attrs) {
        for (const [key, value] of Object.entries(attrs)) {
            if (key === "className") element.className = value;
            else element.setAttribute(key, value);
        }
    }
    for (const child of children) {
        element.append(typeof child === "string" ? document.createTextNode(child) : child);
    }
    return element;
}

/**
 * Create an element and set its innerHTML from a **static** template string.
 *
 * This helper exists to centralise the few places the dashboard needs inline
 * HTML (rich descriptions, formatted paragraphs, links) and make them easy
 * to audit.  Every call site MUST pass a compile-time-constant string — never
 * user input.
 */
export function htmlEl<K extends keyof HTMLElementTagNameMap>(
    tag: K,
    attrs: Record<string, string> | undefined,
    html: string,
): HTMLElementTagNameMap[K] {
    const element = el(tag, attrs);
    element.innerHTML = html;
    return element;
}
