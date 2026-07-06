# General Settings

## Automatic Analysis

- **Automatically Analyze New Media** (Default: Enabled)
  - When enabled, newly added media is automatically queued for segment analysis.
  - To configure the scheduled task timing, see [Scheduled Tasks](https://github.com/intro-skipper/intro-skipper/wiki/Scheduled-Tasks).

- **Re-analyze Settled Seasons** (Default: Disabled)
  - When a season has received no new episodes for the configured delay, the entire season is re-analyzed.
  - Audio fingerprint detection compares episodes against each other, so segments first derived from a partial season improve once the full season is present.
  - Uses cached fingerprints; does not re-decode media.

- **Settled Season Delay (hours)** (Default: 24, visible when Re-analyze Settled Seasons is enabled)
  - Number of hours with no new episode additions before a season is treated as settled and eligible for re-analysis.

- **Update Missing Segments During Scan** (Default: Enabled)
  - During a library scan, updates media segments for any uncached or recently changed media.
  - **Warning:** Disable this if you are using media segment providers other than Intro Skipper.

## Exclusions

- **Excluded Series**
  - Series names to exclude from analysis, matched exactly and case-insensitively.
  - Start typing to pick from series already in your libraries.

- **Excluded Movies**
  - Movie names to exclude from analysis, matched exactly and case-insensitively.

- **Excluded Paths**
  - Filesystem paths to exclude from analysis. Media under these paths will be skipped entirely.

## Segment Types to Scan

Select which types of segments the plugin will detect during analysis:

- **Introduction** — Theme songs and opening sequences
- **Credits** — Ending credits
- **Recap** — "Previously on..." segments
- **Preview** — "Next time..." or preview segments
- **Commercials** — Commercial breaks

## Other Settings

- **Analyze Season 0 (Specials / Extras)** (Default: Disabled)
  - Includes specials and extras (Season 0) in analysis.
  - Note: shows that have both a specials and extras folder identify extras as Season 0, regardless of this setting.

- **Use File Transformation Plugin** (Default: Disabled)
  - Enables integration with the [File Transformation Plugin](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) to patch skip button styles into the Jellyfin web interface.
  - Requires the File Transformation Plugin to be installed separately.

- **Skip Button Hide Delay (seconds)** (Default: 8, visible when File Transformation Plugin is enabled)
  - How long the skip button remains visible before automatically hiding. Set to 0 to keep it always visible.
  - Only applies to the web client.

- **Show Intro Skipper in Main Menu** (Default: Enabled)
  - Adds a link to the Intro Skipper dashboard in the Jellyfin main navigation menu.
  - Save and refresh the client to apply changes.
