# Detection Settings

These settings control how detected segment boundaries are refined and adjusted after initial analysis.

## Silence Detection

- **Enable Silence Detection** (Default: Disabled)
  - Adjusts segment endpoints to the nearest period of silence in the audio track.
  - Useful for achieving clean cuts at natural audio breaks.

- **Noise Tolerance** (Default: -50 dB, visible when silence detection is enabled)
  - Maximum noise level, in negative decibels, still considered silence.
  - Lowering this value (e.g. -60) makes detection stricter and requires quieter audio.

- **Minimum Silence Duration (seconds)** (Default: 0.33, visible when silence detection is enabled)
  - Minimum continuous silence length before it is used as a boundary adjustment point.

## Keyframe and Chapter Snapping

- **Enable Keyframe Snapping** (Default: Enabled)
  - Adjusts segment endpoints to the nearest video keyframe for smoother seek transitions during skipping.

- **Enable Chapter Snapping** (Default: Enabled)
  - Adjusts segment start and end times to the nearest chapter boundary when one is within the adjustment window.

- **Adjustment Window (Inward) (seconds)** (Default: 5.0)
  - Maximum seconds to search toward the interior of a segment when looking for an adjustment point (chapter boundary, silence, or keyframe).
  - Used to tighten (shorten) segment boundaries.

- **Adjustment Window (Outward) (seconds)** (Default: 2.0)
  - Maximum seconds to search away from a segment boundary when looking for an adjustment point.
  - Used to expand (widen) segment boundaries.

- **Snap to Episode Start/End Threshold (seconds)** (Default: 2.0)
  - If a segment boundary falls within this many seconds of the episode's start or end, it is automatically snapped to match the episode boundary.
  - Set to 0 to disable.

## First Episode Handling

- **Ignore Intros for First Episode of a Season** (Default: Disabled)
  - Prevents the skip button from appearing on the first episode of each season, even when an intro segment is detected.
  - The first episode is still analyzed; segment data is simply not returned to clients.

- **Only Ignore First Episode of an Anime Season** (Default: Disabled, visible when the above is enabled)
  - Restricts the first-episode ignore behavior to anime series only.

## Anime Preview Generation

- **Set After-Credits Scene as Preview for Anime** (Default: Disabled)
  - When credits are detected but no preview segment exists, creates a Preview segment spanning from the end of credits to the end of the episode.
  - Intended for anime episodes that include a post-credits stinger or scene.

## Segment Offset Adjustment

These settings apply a fixed time offset to all skip operations, regardless of how boundaries were detected.

- **Intro Start Offset (seconds)** (Default: 0)
  - Number of seconds into the intro that will still play before skipping begins.
  - Example: a value of 3 plays the first 3 seconds of the intro before skipping.

- **Intro End Offset (seconds)** (Default: 0)
  - Number of seconds before the segment's end point at which playback resumes.
  - Example: a value of 3 resumes playback 3 seconds before the detected intro end.
