// One AbortSignal per mounted view is the dashboard's lifetime. main.ts owns
// the mount's controller, the router opens a child per tab, and a tab opens
// children for its own nested views. Fetches, delays, listeners and store
// subscriptions all take the signal, so tearing a view down is one abort().

export function isAbortError(err: unknown): boolean {
    return err instanceof DOMException && err.name === "AbortError";
}

/** For the end of a promise chain: an abort is the view going away, anything else is a bug. */
export function ignoreAbort(err: unknown): void {
    if (!isAbortError(err)) console.error(err);
}

type Listener<Args extends unknown[]> = (...args: Args) => void;

/**
 * Typed listeners whose registration ends with a signal. `Events` maps an event
 * name to the argument tuple its listeners receive.
 */
export function eventBus<Events extends Record<string, unknown[]>>() {
    const listeners: { [E in keyof Events]?: Set<Listener<Events[E]>> } = {};

    return {
        on<E extends keyof Events>(
            event: E,
            callback: Listener<Events[E]>,
            { signal }: { signal: AbortSignal },
        ): void {
            if (signal.aborted) return;
            let set = listeners[event];
            if (!set) {
                set = new Set();
                listeners[event] = set;
            }
            set.add(callback);
            signal.addEventListener("abort", () => set.delete(callback), { once: true });
        },
        emit<E extends keyof Events>(event: E, ...args: Events[E]): void {
            for (const callback of listeners[event] ?? []) {
                callback(...args);
            }
        },
    };
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

/** Resolves after `ms`, or rejects with the signal's reason (an AbortError by default) as soon as `signal` aborts. */
export function delay(ms: number, signal: AbortSignal): Promise<void> {
    return new Promise((resolve, reject) => {
        if (signal.aborted) {
            reject(signal.reason);
            return;
        }
        const onAbort = () => {
            clearTimeout(timer);
            reject(signal.reason);
        };
        const timer = setTimeout(() => {
            signal.removeEventListener("abort", onAbort);
            resolve();
        }, ms);
        signal.addEventListener("abort", onAbort, { once: true });
    });
}

/**
 * Settles like `promise`, unless `signal` aborts first; then it rejects with the
 * signal's reason (an AbortError by default). For awaits on things that cannot take the signal themselves:
 * shared caches, Jellyfin's own client, dialogs.
 */
export function abortable<T>(promise: Promise<T>, signal: AbortSignal): Promise<T> {
    if (signal.aborted) return Promise.reject(signal.reason);
    return new Promise<T>((resolve, reject) => {
        const onAbort = () => reject(signal.reason);
        signal.addEventListener("abort", onAbort, { once: true });
        promise.then(resolve, reject).finally(() => {
            signal.removeEventListener("abort", onAbort);
        });
    });
}
