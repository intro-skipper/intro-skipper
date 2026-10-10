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

test("unset subtitle overrides display and save as disabled", () => {
    const store = createOverrideStore();
    store.load({
        AnalysisPercent: null,
        AnalysisLengthLimit: null,
        PreviewFromCreditsEnd: null,
        SubtitleRecapDetection: null,
        SubtitlePreviewDetection: null,
    });

    assert.equal(store.get("SubtitleRecapDetection"), false);
    assert.equal(store.get("SubtitlePreviewDetection"), false);
    assert.equal(store.values().SubtitleRecapDetection, false);
    assert.equal(store.values().SubtitlePreviewDetection, false);
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
