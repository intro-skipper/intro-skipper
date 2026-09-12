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
One decode of the keyframes from an episode's credits start, reporting black-frame evidence and keyframe visuals together.
_Avoid_: Entropy scan

**Black-frame evidence**:
The per-keyframe black percentage a keyframe scan reports. The basis for detecting roll credits.
_Avoid_: Black frames (for the data), pblack

**Keyframe visuals**:
The per-keyframe entropy and saturation a keyframe scan reports. The basis for detecting credits on a uniform card.
_Avoid_: Entropy data, visual stats, card evidence

### Analysis

**File version**:
The last write time Jellyfin observed for an item's media file, recorded with each analysis record. An analysis whose file version differs from the item's current one no longer describes the file.
_Avoid_: mtime, fingerprint (Chromaprint owns that word), stamp, ETag

**Season key**:
The id a group of items is analyzed and stored under. A movie's key is its own id. An episode's key is its season's id, except for an in-season special, whose key is its host season's id when that season exists and is not virtual.
_Avoid_: Queue key, season-state key, group id

**In-season special**:
An episode stored in Season 0 whose aired season number names another season. Its host season is the series' season with that number, and the special is analyzed with it.
_Avoid_: Interleaved special, cross-season special
