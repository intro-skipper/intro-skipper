# Chapter Detection Settings

Chapter detection uses regular expressions to identify segment types from chapter names in media files. This is useful for files that have properly named chapters.

## Introduction Chapter Pattern

**Default Pattern:** `(^|\s)(Intro|Introduction|OP|Opening)(?![\s:]+End)(\s|:|$)`

Matches chapter names containing:

- "Intro" or "Introduction"
- "OP" (Opening - common in anime)
- Cases like "Intro", "Opening", "Intro Scene"

Examples that match:

- "Intro"
- "Opening Theme"
- "OP1"
- "Opening OP01"

## Credits/Ending Chapter Pattern

**Default Pattern:** `(^|\s)(Credits?|ED|Ending|Outro)(?![\s:]+End)(\s|:|$)`

Matches chapter names containing:

- "Credits" or "Credit"
- "ED" (Ending - common in anime)
- "Ending" or "Outro"

Examples that match:

- "Credits"
- "ED1"
- "Ending Theme"
- "Outro"

## Preview Chapter Pattern

**Default Pattern:** `(^|\s)(Preview|PV|Sneak\s?Peek|Coming\s?(Up|Soon)|Next\s+(time|on|episode)|Extra|Teaser|Trailer)(?!\sEnd)(\s|:|$)`

Matches chapter names containing:

- "Preview" or "PV" (common in anime)
- "Sneak Peek"
- "Coming Up" or "Coming Soon"
- "Next on" or "Next time"
- "Extra", "Teaser", "Trailer"

## Recap Chapter Pattern

**Default Pattern:** `(^|\s)(Re?cap|Sum{1,2}ary|Prev(ious(ly)?)?|(Last|Earlier)(\s\w+)?|Catch[ -]up)(?!\sEnd)(\s|:|$)`

Matches chapter names containing:

- "Recap" or "Cap"
- "Summary"
- "Previously" or "Previously on"
- "Last [episode]"
- "Earlier [time]"
- "Catch-up" or "Catch up"

## Commercial Chapter Pattern

**Default Pattern:** `(^|\s)(Ad(vert(isement)?)?|Commercial|Intermission)(?![\s:]+End)(\s|:|$)`

Matches chapter names containing:

- "Ad", "Advertisement", "Advert"
- "Commercial"
- "Intermission"

## Customizing Patterns

To modify these patterns:

1. Open the Intro Skipper plugin settings
2. Navigate to the **Chapters** tab
3. Edit the regex patterns as needed
4. Use the **Reset to default** button to restore a pattern to its original value
5. Save your settings

### Pattern Syntax

These use standard regular expression (regex) syntax:

- `^` = start of string
- `$` = end of string
- `\s` = whitespace
- `|` = OR operator
- `()` = grouping
- `?!` = negative lookahead

### Pattern Modifiers

The patterns include:

- `(?![\s:]+End)` = Negative lookahead to exclude chapters ending with " End" or ": End" (prevents matching "Intro End Screen")
- Case-insensitive matching

### Testing Changes

After modifying patterns:

1. Clear the fingerprint cache to force re-analysis
2. Re-run analysis on a test episode
3. Verify results in the Segment Editor

## Tips for Better Detection

- Keep patterns simple and specific to your media library
- Test patterns with sample episodes first
- Consider your chapter naming conventions
- Use the Segment Editor to manually verify detections
- Document any custom patterns you create for future reference
