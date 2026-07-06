# Analysis Settings

> **Note:** Changes to these settings require regenerating media segments to take effect. Per the MediaSegments API, records are updated individually and may be slow.

## Analysis Parameters

- **Prefer Chromaprint Analysis** (Default: Disabled)
  - Use only audio fingerprinting (Chromaprint) for analysis, bypassing chapter-based detection.
  - More accurate than chapter analysis in many cases, but slower.
  - Setting an analysis mode per-season in the Timestamps editor will override this setting.

- **Ignore Duration Limits for Chapters** (Default: Disabled)
  - Allows segments detected from chapters to extend beyond the configured minimum/maximum duration limits.
  - Useful when chapters are unusually long but correctly named.

- **Percent of Media to Analyze** (Default: 25%, Range: 1–50%)
  - Limits analysis to this percentage of each item's runtime. For example, 25% restricts analysis to the first quarter of each item.

- **Maximum Runtime to Analyze (minutes)** (Default: 10)
  - Caps analysis at this number of minutes per item. The actual limit applied is the minimum of `(duration × percent)` and this value.
  - Increasing this setting will cause analysis to take longer.

## Segment Duration Limits

For each segment type, minimum and maximum durations define what qualifies as a valid detection. Audio or chapters that fall outside these ranges are not reported as segments.

| Segment Type | Setting | Default |
|---|---|---|
| Introduction | Minimum duration (seconds) | 15 |
| Introduction | Maximum duration (seconds) | 120 |
| Credits | Minimum duration (seconds) | 15 |
| Credits | Maximum duration (seconds) | 450 |
| Credits (movies) | Maximum duration (seconds) | 900 |
| Recap (chapter-based) | Minimum duration (seconds) | 15 |
| Recap (chapter-based) | Maximum duration (seconds) | 120 |
| Recap (detected) | Minimum duration (seconds) | 15 |
| Recap (detected) | Maximum duration (seconds) | 120 |
| Preview | Minimum duration (seconds) | 15 |
| Preview | Maximum duration (seconds) | 120 |
| Commercial | Minimum duration (seconds) | 15 |
| Commercial | Maximum duration (seconds) | 120 |

> **Recap (chapter-based)** limits apply when a chapter name matches the recap regex pattern.  
> **Recap (detected)** limits apply when black frame or chromaprint detection is used as a fallback.  
> **Preview** and **Commercial** duration limits are only used when chapter detection is active (i.e. Ignore Duration Limits for Chapters is disabled).
