import assert from "node:assert/strict";
import test from "node:test";
import { createOverrideStore, overrideSchema } from "../src/components/season-overrides.ts";

const subtitleModes = ["SubtitleRecapDetection", "SubtitlePreviewDetection"];

test("subtitle override selects contain only enabled and disabled", () => {
    for (const field of subtitleModes) {
        assert.deepEqual(
            overrideSchema[field].options.map((option) => option.label),
            ["Enabled", "Disabled"],
        );
    }
});

test("unset subtitle overrides retain null for global inheritance", () => {
    const store = createOverrideStore();
    store.load({
        AnalysisPercent: null,
        AnalysisLengthLimit: null,
        PreviewFromCreditsEnd: null,
        SubtitleRecapDetection: null,
        SubtitlePreviewDetection: null,
    });

    assert.equal(store.get("SubtitleRecapDetection"), null);
    assert.equal(store.get("SubtitlePreviewDetection"), null);
    assert.equal(store.values().SubtitleRecapDetection, null);
    assert.equal(store.values().SubtitlePreviewDetection, null);
    assert.equal(overrideSchema.SubtitleRecapDetection.nullDisplayValue(), false);
    assert.equal(overrideSchema.SubtitlePreviewDetection.nullDisplayValue(), false);

    store.set("AnalysisPercent", 30);
    assert.equal(store.values().SubtitleRecapDetection, null);
    assert.equal(store.values().SubtitlePreviewDetection, null);
});

test("subtitle overrides preserve explicit enabled and disabled values", () => {
    const store = createOverrideStore();
    store.load({
        AnalysisPercent: null,
        AnalysisLengthLimit: null,
        PreviewFromCreditsEnd: null,
        SubtitleRecapDetection: true,
        SubtitlePreviewDetection: false,
    });

    assert.equal(store.get("SubtitleRecapDetection"), true);
    assert.equal(store.get("SubtitlePreviewDetection"), false);
});
