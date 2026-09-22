// The language every form field is declared in, and the store shape a control
// binds to. The plugin configuration (config/schema.ts) and the per-season
// overrides (components/season-overrides.ts) are two schemas in this language,
// edited through two stores that satisfy FieldStore.

/** A select choice. A function label is re-read whenever the store loads or changes. */
type SelectOption = {
    readonly value: string | boolean | null;
    readonly label: string | (() => string);
};

type FieldBase = {
    readonly label: string;
    /** Static HTML. Never built from user input. */
    readonly description?: string;
    /** Static HTML. Never built from user input. */
    readonly warning?: string;
};

export type FieldSpec =
    | (FieldBase & { readonly kind: "checkbox" })
    | (FieldBase & {
          readonly kind: "number";
          /** Doubles as the HTML min attribute and the validation floor. */
          readonly min?: number;
          /** Doubles as the HTML max attribute and the validation ceiling. */
          readonly max?: number;
          /** The HTML step attribute. An integer step also makes a fraction a validation error. */
          readonly step?: number;
          /** An empty input means "no value" (null) rather than "still typing". */
          readonly optional?: true;
          /** A function placeholder is re-read whenever the store loads or changes. */
          readonly placeholder?: string | (() => string);
      })
    | (FieldBase & { readonly kind: "text"; readonly placeholder?: string })
    /** A regular expression with a reset-to-default; the default is also the placeholder. */
    | (FieldBase & { readonly kind: "regex"; readonly default: string })
    | (FieldBase & { readonly kind: "select"; readonly options: readonly SelectOption[] })
    /** A list of trimmed, non-empty strings. */
    | (FieldBase & { readonly kind: "list"; readonly placeholder?: string });

export type FieldKind = FieldSpec["kind"];
export type FieldSchema = Record<string, FieldSpec>;

export type ValueOf<S extends FieldSpec> = S extends { kind: "checkbox" }
    ? boolean
    : S extends { kind: "number"; optional: true }
      ? number | null
      : S extends { kind: "number" }
        ? number
        : S extends { kind: "select"; options: readonly { value: infer V }[] }
          ? V
          : S extends { kind: "text" | "regex" }
            ? string
            : S extends { kind: "list" }
              ? string[]
              : never;

/** The record a schema describes: one editable value per field. */
export type ValuesOf<S extends FieldSchema> = { -readonly [K in keyof S]: ValueOf<S[K]> };

/** Keys of `S` whose entry has the given kind. */
export type KeysOfKind<S extends FieldSchema, Kd extends FieldKind> = {
    [K in keyof S]: S[K] extends { kind: Kd } ? K : never;
}[keyof S];

export type FieldStoreEvents<K> = {
    loaded: [];
    changed: [{ field: K }];
    validation: [{ field: K; error: string | null }];
};

/**
 * What a control needs from the record it edits. The methods are typed over the
 * whole record so any store with precise per-key methods satisfies it; the
 * control reads and writes by the kind it rendered.
 */
export type FieldStore<V> = {
    isLoaded(): boolean;
    get(field: keyof V): V[keyof V];
    set(field: keyof V, value: V[keyof V]): void;
    subscribe<E extends keyof FieldStoreEvents<keyof V>>(
        event: E,
        callback: (...args: FieldStoreEvents<keyof V>[E]) => void,
        options: { signal: AbortSignal },
    ): void;
};

export function labelText(label: string | (() => string)): string {
    return typeof label === "function" ? label() : label;
}
