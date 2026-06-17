export function splitConfiguredList(value: string): string[] {
    const items: string[] = [];
    const seen = new Set<string>();
    let current = "";
    let inQuotes = false;
    let onlyWhitespaceInField = true;

    function appendItem(): void {
        const item = current.trim();
        current = "";
        onlyWhitespaceInField = true;
        if (!item) return;

        const key = item.toLowerCase();
        if (seen.has(key)) return;

        seen.add(key);
        items.push(item);
    }

    for (let i = 0; i < value.length; i++) {
        const char = value[i];
        if (char === '"' && inQuotes) {
            if (value[i + 1] === '"') {
                current += '"';
                i++;
            } else {
                inQuotes = false;
            }
        } else if (char === '"' && onlyWhitespaceInField) {
            current = "";
            inQuotes = true;
        } else if (char === "," && !inQuotes) {
            appendItem();
        } else {
            current += char;
            if (char.trim()) {
                onlyWhitespaceInField = false;
            }
        }
    }

    appendItem();
    return items;
}

export function formatConfiguredList(items: Iterable<string>): string {
    const formattedItems: string[] = [];
    const seen = new Set<string>();

    for (const rawItem of items) {
        const item = rawItem.trim();
        if (!item) continue;

        const key = item.toLowerCase();
        if (seen.has(key)) continue;

        seen.add(key);
        if (/[,"\r\n]/.test(item)) {
            formattedItems.push('"' + item.replaceAll('"', '""') + '"');
        } else {
            formattedItems.push(item);
        }
    }

    return formattedItems.join(", ");
}
