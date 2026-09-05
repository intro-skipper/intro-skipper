export function formatTime(totalSeconds: number): string {
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = Math.floor(totalSeconds % 60);
    const milliseconds = Math.floor((totalSeconds - Math.floor(totalSeconds)) * 1000);

    const parts: string[] = [];
    if (hours > 0) parts.push(hours + "h");
    if (minutes > 0) parts.push(minutes + "m");
    if (seconds > 0 || (parts.length === 0 && milliseconds === 0)) parts.push(seconds + "s");
    if (milliseconds > 0) parts.push(milliseconds + "ms");
    return parts.join(" ");
}

/**
 * Parses a time input into seconds. Accepts plain seconds ("95", "95.5"),
 * minutes:seconds ("1:35.5") and hours:minutes:seconds ("1:02:03.250").
 * Returns null for malformed or negative input.
 */
export function parseTimeInput(value: string): number | null {
    const trimmed = value.trim();
    if (!trimmed) return null;

    const parts = trimmed.split(":");
    if (parts.length > 3) return null;

    let seconds = 0;
    for (const [index, part] of parts.entries()) {
        // Only the last component may carry a fraction; the rest are integers.
        const pattern = index === parts.length - 1 ? /^\d+(\.\d+)?$/ : /^\d+$/;
        if (!pattern.test(part)) return null;
        const component = Number(part);
        // Base-60 positions (seconds, and minutes in h:mm:ss) must stay < 60.
        if (index > 0 && component >= 60) return null;
        seconds = seconds * 60 + component;
    }

    return Number.isFinite(seconds) ? seconds : null;
}

/**
 * Canonical editable form of a time: "m:ss[.fff]" (fraction only when
 * non-zero, trailing zeros trimmed) with an hours prefix when needed;
 * round-trips through parseTimeInput.
 */
export function formatTimeInput(totalSeconds: number): string {
    // Round to milliseconds FIRST so 119.9996 becomes 2:00 rather than 1:60.
    let ms = Math.round(totalSeconds * 1000);
    const hours = Math.floor(ms / 3_600_000);
    ms -= hours * 3_600_000;
    const minutes = Math.floor(ms / 60_000);
    ms -= minutes * 60_000;
    const seconds = ms / 1000;
    const secondsText = (seconds < 10 ? "0" : "") + String(seconds);

    if (hours > 0) {
        return hours + ":" + String(minutes).padStart(2, "0") + ":" + secondsText;
    }
    return minutes + ":" + secondsText;
}

export async function mapWithConcurrency<T, R>(
    items: T[],
    limit: number,
    mapper: (item: T, index: number) => Promise<R>,
): Promise<R[]> {
    const results: R[] = Array.from({ length: items.length }) as R[];
    let nextIndex = 0;

    async function worker(): Promise<void> {
        while (true) {
            const currentIndex = nextIndex;
            nextIndex += 1;
            if (currentIndex >= items.length) {
                return;
            }
            results[currentIndex] = await mapper(items[currentIndex], currentIndex);
        }
    }

    const workerCount = Math.min(limit, items.length);
    await Promise.all(Array.from({ length: workerCount }, () => worker()));
    return results;
}

export function delay(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
}

export function errorText(err: unknown): string {
    return err instanceof Error ? err.message : "Unknown error";
}

export function pluralize(count: number, singular: string, plural = singular + "s"): string {
    return count + " " + (count === 1 ? singular : plural);
}

/** Show name with the production year in parentheses when known. */
export function showTitle(show: { Name: string; ProductionYear: number | null }): string {
    return show.ProductionYear ? show.Name + " (" + show.ProductionYear + ")" : show.Name;
}
