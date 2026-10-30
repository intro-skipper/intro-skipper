# Intro Skipper

A Jellyfin plugin that finds intro, credits, recap and preview segments in episodes and mirrors them into Jellyfin's MediaSegments so clients can skip them.

## Language

### Credits

**Credits**:
The end-of-episode material a viewer wants to skip: credit cards, the closing song, distributor and dubbing cards. Usually one run to the end of the file, sometimes split by an epilogue scene. Stored as one or more Outro segments.
_Avoid_: Outro (in prose), end credits, closing

**Styled credits**:
The part of the credits rendered over artwork or a designed background rather than plain black. Not detectable by black-frame evidence; a near-uniform low-saturation card is detectable by keyframe visuals.
_Avoid_: Art credits, themed credits, part one

**Roll credits**:
The part of the credits rendered as text on a black background, usually scrolling. Detectable by black-frame evidence.
_Avoid_: Black credits, scroll, part two

**Credits candidate**:
One analyzer's proposed time range for an episode's credits before any combination with other analyzers' proposals.
_Avoid_: Detection, hit, match

**Keyframe scan**:
One decode of the keyframes from an episode's credits start, reporting black-frame evidence and keyframe visuals together, one page per keyframe.
_Avoid_: Entropy scan

**Page**:
One keyframe of the keyframe scan: its black-frame row and its keyframe visual, when it has one. Its time is the black-frame time.
_Avoid_: Frame, row (for the pair)

**Keyframe analyzer**:
The analyzer over one keyframe scan. It emits black-frame candidates for roll credits and at most one card run; `docs/credits.md` has its rules.
_Avoid_: Black-frame analyzer (for the class), card analyzer

**Black-frame evidence**:
The per-keyframe black percentage a keyframe scan reports. The basis for detecting roll credits.
_Avoid_: Black frames (for the data), pblack

**Keyframe visuals**:
The per-keyframe luma percentiles, saturation and frame difference a keyframe scan reports. The basis for detecting credits on a uniform card, and the frame difference nominates the cut start.
_Avoid_: Entropy data, visual stats, card evidence

**Card credits**:
Credits rendered as text on a near-uniform low-saturation card: black, white, grey or muted colour. Detectable from keyframe visuals.
_Avoid_: Non-black credits, entropy credits, uniform-card credits

**Cut start**:
Where a credits part starts with boundary refinement on: the frame between its first page and the page before that carries most of the change between the two keyframes, or the first page when no frame does.
_Avoid_: Lead-in probe, boundary probe

**Card kind**:
What a page is to the card run: content, card or black card. The keyframe analyzer sets it after probing.
_Avoid_: Keyframe kind, page type

**Roll page**:
A page of roll credits that dense lettering keeps under the black threshold: black at its darkest tenth, colourless and lettered. A black scene the keyframe analyzer accepts ends at its roll end, the last of the black pages and roll pages that follow it without a break.
_Avoid_: Dense page, text page

### Analysis

**File version**:
The last write time Jellyfin observed for an item's media file, recorded with each analysis record. A mode with active subtitle detection records a version that also covers the text subtitle files it reads next to the media file or in the item's internal metadata folder. An analysis whose file version differs from the item's current one no longer describes the files it read.
_Avoid_: mtime, fingerprint (Chromaprint owns that word), stamp, ETag

**File hash**:
A hash of an item's media file size and the bytes at its end. Two items with the same file hash hold the same file, so times stored for one apply to the other. A remux changes it.
_Avoid_: fingerprint (Chromaprint owns that word), checksum, file version (that is the write time)

**Orphan**:
An item id that still has plugin rows but that the server no longer knows, as after a rename, a move or a changed library path.
_Avoid_: stale item, gone item

**Rebind**:
Moving an orphan's rows to the one live item with the same file hash, so its segments, tombstones and analysis survive the new id.
_Avoid_: migrate, relink, remap

**Season key**:
The id a group of items is analyzed and stored under. A movie's key is its own id. An episode's key is its season's id, except for an in-season special, whose key is its host season's id when that season resolves with episodes of its own.
_Avoid_: Queue key, season-state key, group id

**In-season special**:
An episode stored in Season 0 whose aired season number names another season. Its host season is the season the series' episodes with that number resolved to, and the special is analyzed with it.
_Avoid_: Interleaved special, cross-season special

**Analysis request**:
One unit of work handed to the analysis queue. Three kinds: a changed item from the library watcher, a manual scan of a season from the dashboard, and a library pass from the scheduled task.
_Avoid_: Job, task (Jellyfin owns that word), ticket

**Pass**:
One run of the analyzer over a set of seasons. Passes never overlap; a pass waits for the pass in flight.
_Avoid_: Run (for the noun), scan (the dashboard's word for a manual pass), batch

**Quiet period**:
The time the analysis queue waits after the most recent changed item before starting their pass. Sixty seconds, measured from the change, so an item that waited behind a running pass runs as soon as it ends.
_Avoid_: Debounce, delay, cooldown

**Feeder**:
Anything that hands an analysis request to the queue: the library watcher, the scheduled task, and the manual scan endpoint.
_Avoid_: Producer, trigger, source

**Chapter source**:
Where analysis reads an item's chapters: Jellyfin's chapter table by default, or, with `UseEmbeddedChaptersForAnalysis`, the chapters embedded in the media file, which leaves out chapters another plugin added to the table. The embedded read is shaped as Jellyfin's own probe stores chapters, so for a file no other plugin touched both sources hold the same list. A pass reads each episode's chapters from it once, before any mode runs, and every analyzer reads those.
_Avoid_: chapter authority (the credits rule that a recognized credits chapter no story follows settles the episode), chapter provider

### Segments

**Tombstone**:
An automatic segment the user deleted, kept as a suppressed row. It refuses automatic segments of its item and mode that re-detect the deleted range, up to 3 s past either end. It protects nothing the user made: deleting a user segment removes it outright.
_Avoid_: Lock, timestamp lock

**User segment**:
A segment a person added through the segment editor or the segments API, or an automatic segment whose range they moved. It settles its mode for its item, so analysis leaves that mode alone, and no erase removes it.
_Avoid_: Manual segment, manual edit
