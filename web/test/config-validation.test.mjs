import assert from "node:assert/strict";
import test from "node:test";
import { validateSpec } from "../src/config/validate.ts";
import { configSchema } from "../src/config/schema.ts";

test("credits offsets accept only whole seconds", () => {
    for (const key of ["CreditsStartOffset", "CreditsEndOffset"]) {
        const spec = configSchema[key];

        assert.equal(spec.step, 1);
        assert.equal(validateSpec(spec, 3), null);
        assert.equal(validateSpec(spec, 0.5), "Must be a whole number");
    }
});
