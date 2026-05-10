import { en } from "./en.ts";

/** All valid translation keys, derived from the default English locale. */
export type LocaleKey = keyof typeof en;

/** Substitution parameters for interpolated strings. */
export type LocaleParams = Record<string, string | number>;

const locales: Record<string, Record<LocaleKey, string>> = { en };

function getCurrentStrings(): Record<LocaleKey, string> {
    const lang = document.documentElement.lang.toLowerCase();
    const locale = lang.split("-")[0];
    return locales[lang] ?? locales[locale] ?? en;
}

/**
 * Looks up a localized string by key and substitutes any `{name}`
 * placeholders with the values from `params`.
 *
 * @param key    - A key from the English locale (type-checked at compile time).
 * @param params - Optional map of placeholder names to substitution values.
 */
export function t(key: LocaleKey, params?: LocaleParams): string {
    const str = getCurrentStrings()[key] ?? en[key] ?? key;
    if (!params) return str;
    return str.replace(/\{(\w+)\}/g, (_, k: string) => String(params[k] ?? ""));
}
