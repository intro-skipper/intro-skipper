# Anime Settings

These settings provide special handling for anime series, which often follow different intro and credits conventions than other content.

## First Episode Handling

- **Ignore Intros for First Episode of a Season** (Default: Disabled)
  - Prevents the skip button from appearing on the first episode of each season, even when an intro segment is detected.
  - The first episode is still analyzed; segment data is simply not returned to clients for that episode.
  - Useful because many anime do not have an opening theme in the pilot episode.

- **Only Ignore First Episode of an Anime Season** (Default: Disabled)
  - Restricts the above behavior to anime series only, leaving other series unaffected.
  - Must be used together with "Ignore Intros for First Episode of a Season".

## Post-Credits Preview Generation

- **Set After-Credits Scene as Preview for Anime** (Default: Disabled)
  - When a credits segment is detected but no preview segment exists, automatically creates a Preview segment covering the time from the end of credits to the end of the episode.
  - Intended for anime episodes that include a post-credits stinger or scene shown after the ending theme.

## Chapter Detection for Anime

Anime commonly uses Japanese shorthand in chapter names. The default chapter detection patterns already include:

- `OP` for Opening (matches as an introduction)
- `ED` for Ending (matches as credits)
- `PV` for Preview (matches as a preview)

These patterns can be customized in the [Chapter Detection Patterns](https://github.com/intro-skipper/intro-skipper/wiki/Chapter-Detection-Patterns) settings if your files use different naming conventions.
