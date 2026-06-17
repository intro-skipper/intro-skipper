// Exclusion lists are stored as plain comma-separated values. Entries are trimmed and
// de-duplicated case-insensitively; a comma is always a separator (commas inside a value
// are not supported, matching the backend's split behavior).
export function splitConfiguredList(value: string): string[] {
    const items: string[] = [];
    const seen = new Set<string>();

    for (const raw of value.split(",")) {
        const item = raw.trim();
        if (!item) continue;

        const key = item.toLowerCase();
        if (seen.has(key)) continue;

        seen.add(key);
        items.push(item);
    }

    return items;
}

export function formatConfiguredList(items: Iterable<string>): string {
    const formatted: string[] = [];
    const seen = new Set<string>();

    for (const rawItem of items) {
        const item = rawItem.trim();
        if (!item) continue;

        const key = item.toLowerCase();
        if (seen.has(key)) continue;

        seen.add(key);
        formatted.push(item);
    }

    return formatted.join(", ");
}
