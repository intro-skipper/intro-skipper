import { en } from "./en.ts";

/** All valid translation keys, derived from the default English locale. */
export type LocaleKey = keyof typeof en;

/** Substitution parameters for interpolated strings. */
export type LocaleParams = Record<string, string | number>;

let currentStrings: Record<string, string> = en as Record<string, string>;

/**
 * Loads an additional locale by merging it over the English defaults.
 * Any key that is absent from the provided locale falls back to English.
 *
 * @param strings - A flat key→value map of translated strings.
 */
export function loadLocale(strings: Record<string, string>): void {
    currentStrings = { ...(en as Record<string, string>), ...strings };
}

/**
 * Looks up a localized string by key and substitutes any `{name}`
 * placeholders with the values from `params`.
 *
 * Falls back to the raw key if the key is not found in the current locale.
 *
 * @param key    - A key from the English locale (type-checked at compile time).
 * @param params - Optional map of placeholder names to substitution values.
 */
export function t(key: LocaleKey, params?: LocaleParams): string {
    const str = currentStrings[key] ?? key;
    if (!params) return str;
    return str.replace(/\{(\w+)\}/g, (_, k: string) => String(params[k] ?? ""));
}
