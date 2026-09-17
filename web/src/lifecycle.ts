// One AbortSignal per mounted view is the dashboard's lifetime. main.ts owns
// the mount's controller, the router opens a child per tab, and a tab opens
// children for its own nested views. Fetches, delays, listeners and store
// subscriptions all take the signal, so tearing a view down is one abort().

/** The error an aborted signal produces, the same shape fetch rejects with. */
export function abortError(): DOMException {
    return new DOMException("The operation was aborted.", "AbortError");
}

export function isAbortError(err: unknown): boolean {
    return err instanceof DOMException && err.name === "AbortError";
}

/** For the end of a promise chain: an abort is the view going away, anything else is a bug. */
export function ignoreAbort(err: unknown): void {
    if (!isAbortError(err)) console.error(err);
}

/** A controller that aborts when `parent` does. Abort it early to end a nested lifetime. */
export function childScope(parent: AbortSignal): AbortController {
    const child = new AbortController();
    if (parent.aborted) {
        child.abort();
    } else {
        parent.addEventListener("abort", () => child.abort(), { once: true, signal: child.signal });
    }
    return child;
}

/** Resolves after `ms`, or rejects with an AbortError as soon as `signal` aborts. */
export function delay(ms: number, signal: AbortSignal): Promise<void> {
    return new Promise((resolve, reject) => {
        if (signal.aborted) {
            reject(abortError());
            return;
        }
        const onAbort = () => {
            clearTimeout(timer);
            reject(abortError());
        };
        const timer = setTimeout(() => {
            signal.removeEventListener("abort", onAbort);
            resolve();
        }, ms);
        signal.addEventListener("abort", onAbort, { once: true });
    });
}

/**
 * Settles like `promise`, unless `signal` aborts first; then it rejects with an
 * AbortError. For awaits on things that cannot take the signal themselves:
 * shared caches, Jellyfin's own client, dialogs.
 */
export function abortable<T>(promise: Promise<T>, signal: AbortSignal): Promise<T> {
    if (signal.aborted) return Promise.reject(abortError());
    return new Promise<T>((resolve, reject) => {
        const onAbort = () => reject(abortError());
        signal.addEventListener("abort", onAbort, { once: true });
        promise.then(resolve, reject).finally(() => {
            signal.removeEventListener("abort", onAbort);
        });
    });
}
