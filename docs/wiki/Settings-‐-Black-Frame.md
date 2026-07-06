# Black Frame Detection Settings

Black frame detection finds sequences of very dark frames to identify credits and recap boundaries. It is used as a complement to or fallback for audio fingerprint and chapter-based analysis.

## Recap Detection

- **Detect Recap Using Black Frames** (Default: Disabled)
  - When chapter-based recap detection finds nothing, attempts to mark a recap from 0:00 to the latest black frame within the configured recap duration limits and before the detected intro.

## Credits Detection

- **Use Alternative Black Frame Analyzer** (Default: Disabled)
  - Enables an alternative approach to black frame credits detection.
  - When **enabled**, the following additional options are available:
    - **Refine Credits Boundary** (Default: Enabled) — Uses frame-level analysis to find the exact frame where credits begin, rather than relying on keyframe-only accuracy. Disable for faster analysis.
    - **Detect Non-Black Credits** (Default: Enabled) — Also detects credits displayed on a near-uniform, low-saturation card (black, white, grey, or muted color). Vivid or highly saturated backgrounds are not flagged.
  - When **disabled**, the following option is available instead:
    - **Use Chapter Markers for Credits Detection** (Default: Enabled) — Combines black frame detection with chapter markers to reduce false positives.

## Black Frame Thresholds

- **Minimum Percentage of Black Pixels** (Default: 85%)
  - Minimum percentage of a frame's pixels that must be below the threshold before the frame is counted as a black frame.

- **Black Frame Threshold** (Default: 28)
  - Pixel brightness value below which a pixel is classified as black.
  - Range: 16–255. Lower values require frames to be darker to qualify.
