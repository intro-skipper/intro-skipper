// The one declaration of every plugin setting the dashboard edits. The
// PluginConfig type, the validator, the form controls and the tabs all read
// from here. Which tab shows a field, and in what order, is the tab's own
// business; a field's visibility rule stays with the tab too, because it needs
// the config store.

import type { FieldKind, FieldSchema, KeysOfKind, ValuesOf } from "./field-spec.ts";

const PATTERN_TAIL = "(?![\\s:]+End)(\\s|:|$)";

export const configSchema = {
    // General
    AutoDetectIntros: {
        kind: "checkbox",
        label: "Automatically Analyze New Media",
        description:
            'If enabled, new media will be automatically analyzed for skippable segments when added to the library<br/><br/>Note: To configure the scheduled task, see <a href="#/dashboard/tasks">scheduled tasks</a>.',
    },
    ReanalyzeSettledSeasons: {
        kind: "checkbox",
        label: "Re-analyze settled seasons",
        description:
            "When a season has no new episodes for the configured delay, re-analyze the season (using cached fingerprints) so segments are recomputed against the full season. Media is not re-decoded.",
    },
    UseSettledSeasonChromaprintConsensus: {
        kind: "checkbox",
        label: "Require Chromaprint consensus",
        description:
            "During settled-season re-analysis, require locally connected groups of episodes to share an intro or credits pattern for the pattern to be accepted as a segment.",
    },
    SettledSeasonChromaprintConsensusEpisodes: {
        kind: "number",
        label: "Consensus episode threshold",
        min: 2,
        max: 1000,
        step: 1,
        description:
            "Target number of distinct episodes required to confirm a shared pattern. Comparable pattern groups can lower the target so mid-season theme changes can still qualify; one group covering at least three quarters of usable fingerprints can also qualify below the target.",
    },
    SettledSeasonDelayHours: {
        kind: "number",
        label: "Settled season delay (hours)",
        min: 0,
        max: 87600,
        step: 1,
        description:
            "Treat a season as settled after this many hours without newly added episodes. Default is 24; increase this for weekly releases.",
    },
    UpdateMediaSegments: {
        kind: "checkbox",
        label: "Update Missing Segments During Scan",
        description:
            "Enable this option to update media segments for any uncached media during a library scan, including recently added, modified, or previously skipped (but not ignored) files.<br/><b>Warning:</b> This should be disabled if you're using media segment providers other than Intro Skipper.",
    },
    SeriesExclusions: {
        kind: "list",
        label: "Excluded series",
        placeholder: "Series name",
        description:
            "Series names matched exactly, case-insensitively. Start typing to pick from your libraries.",
    },
    MovieExclusions: {
        kind: "list",
        label: "Excluded movies",
        placeholder: "Movie name",
        description:
            "Movie names matched exactly, case-insensitively. Start typing to pick from your libraries.",
    },
    PathExclusions: {
        kind: "list",
        label: "Excluded paths",
        placeholder: "/media/library",
        description:
            "Exact paths or child paths under a listed root. Storage folders are available as suggestions when the server reports them.",
    },
    ScanIntroduction: { kind: "checkbox", label: "Introduction" },
    ScanCredits: { kind: "checkbox", label: "Credits" },
    ScanRecap: { kind: "checkbox", label: "Recap" },
    ScanPreview: { kind: "checkbox", label: "Preview" },
    ScanCommercial: { kind: "checkbox", label: "Commercials" },
    AnalyzeSeasonZero: {
        kind: "checkbox",
        label: "Analyze Season 0 (Specials / Extras)",
        description:
            "Note: Shows containing both a specials and extra folder will identify extras as season 0 and ignore specials, regardless of this setting.",
    },
    UseFileTransformationPlugin: {
        kind: "checkbox",
        label: "Use File Transformation Plugin to patch the web interface",
    },
    SkipbuttonHideDelay: {
        kind: "number",
        label: "Skip button hide delay (in seconds)",
        min: 0,
        max: 1000,
        description:
            "Time in seconds before the skip button automatically hides. Set to 0 for persistent skip button (never hides).",
        warning:
            "Note: This setting only applies to the web client (browsers, LG webOS, Android with web player enabled, etc). May require a refresh or clearing cache to see changes.",
    },
    AutoSkipIntro: {
        kind: "checkbox",
        label: "Auto-skip intros",
        description:
            "Automatically skip intro segments without showing the skip button. Users who have explicitly set a preference in their Jellyfin settings will keep it.",
    },
    AutoSkipCredits: {
        kind: "checkbox",
        label: "Auto-skip credits/outros",
        description:
            "Automatically skip credits/outro segments without showing the skip button. Users who have explicitly set a preference in their Jellyfin settings will keep it.",
    },
    SkipButtonVisibleSeconds: {
        kind: "number",
        label: "Hide skip button before segment end (seconds)",
        min: 0,
        max: 600,
        step: 1,
        description:
            "Hide the skip button this many seconds before the segment ends. Set to 0 to disable this end-relative limit. The normal skip button hide delay still applies.",
    },
    EnableMainMenu: {
        kind: "checkbox",
        label: "Show Intro Skipper in Main Menu",
        description:
            "Toggle the Intro Skipper entry in the server's main navigation. Save and refresh the client (or clear cache) to apply.",
    },

    // Analysis
    PreferChromaprint: {
        kind: "checkbox",
        label: "Prefer Chromaprint Analysis",
        description:
            "Run chromaprint before the chapter analyzer for intros and recaps. Credits ignore this setting.",
    },
    EnhanceChapterCredits: {
        kind: "checkbox",
        label: "Enhance chapter credits",
        description:
            "When enabled, combine recognized credits chapters with black-frame and chromaprint results, which can extend the credits or find additional blocks. Credits are not re-analyzed until the next scan.",
    },
    FullLengthChapters: {
        kind: "checkbox",
        label: "Ignore duration limits for chapters",
        description:
            "Allow segments to extend to the end of a chapter when the marker exceeds other user settings, such as percentage or duration.",
    },
    AnalysisPercent: {
        kind: "number",
        label: "Percent of media to analyze",
        min: 1,
        max: 50,
        description:
            "Analysis will be limited to this percentage of each item's runtime. For example, a value of 25 (the default) will limit analysis to the first quarter of each item.",
    },
    AnalysisLengthLimit: {
        kind: "number",
        label: "Maximum runtime to analyze (in minutes)",
        min: 1,
        description:
            "Analysis will be limited to this amount of each item's runtime. For example, a value of 10 (the default) will limit analysis to the first 10 minutes of each item.",
    },
    MinimumRecapDuration: {
        kind: "number",
        label: "Minimum recap duration (in seconds)",
        min: 1,
        description:
            "Recap chapters which are shorter than this duration will not be considered a recap.",
    },
    MaximumRecapDuration: {
        kind: "number",
        label: "Maximum recap duration (in seconds)",
        min: 1,
        description:
            "Recap chapters which are longer than this duration will not be considered a recap.",
    },
    MinimumRecapDetectionDuration: {
        kind: "number",
        label: "Minimum detected recap duration (in seconds)",
        min: 1,
        description: "Blackframe/chromaprint recaps shorter than this duration will not be detected.",
    },
    MaximumRecapDetectionDuration: {
        kind: "number",
        label: "Maximum detected recap duration (in seconds)",
        min: 1,
        description:
            "Blackframe/chromaprint recaps longer than this duration will be capped or ignored.",
    },
    MinimumIntroDuration: {
        kind: "number",
        label: "Minimum introduction duration (in seconds)",
        min: 1,
        description:
            "Segments or similar sounding audio which is shorter than this duration will not be considered an introduction.",
    },
    MaximumIntroDuration: {
        kind: "number",
        label: "Maximum introduction duration (in seconds)",
        min: 1,
        description:
            "Segments or similar sounding audio which is longer than this duration will not be considered an introduction.",
    },
    MinimumCreditsDuration: {
        kind: "number",
        label: "Minimum credits duration (in seconds)",
        min: 1,
        description:
            "Segments or similar sounding audio which is shorter than this duration will not be considered credits.",
    },
    MaximumCreditsDuration: {
        kind: "number",
        label: "Maximum credits duration (in seconds)",
        min: 1,
        description:
            "Segments or similar sounding audio which is longer than this duration will not be considered credits.",
    },
    MaximumMovieCreditsDuration: {
        kind: "number",
        label: "Maximum movie credits duration (in seconds)",
        min: 1,
        description: "Segments longer than this duration will not be considered movie credits.",
    },
    MinimumPreviewDuration: {
        kind: "number",
        label: "Minimum preview duration (in seconds)",
        min: 1,
        description: "Segments which are shorter than this duration will not be considered a preview.",
    },
    MaximumPreviewDuration: {
        kind: "number",
        label: "Maximum preview duration (in seconds)",
        min: 1,
        description: "Segments which are longer than this duration will not be considered a preview.",
    },
    MinimumCommercialDuration: {
        kind: "number",
        label: "Minimum commercial duration (in seconds)",
        min: 1,
        description:
            "Segments which are shorter than this duration will not be considered a commercial.",
    },
    MaximumCommercialDuration: {
        kind: "number",
        label: "Maximum commercial duration (in seconds)",
        min: 1,
        description:
            "Segments which are longer than this duration will not be considered a commercial.",
    },

    // Detection
    SnapToKeyframe: {
        kind: "checkbox",
        label: "Enable keyframe snapping",
        description:
            "When enabled, segment endpoints will be adjusted to the nearest video keyframe for smoother seek transitions during skipping.",
    },
    AdjustIntroBasedOnChapters: {
        kind: "checkbox",
        label: "Enable chapter snapping",
        description:
            "When enabled, segment start and end times will be adjusted to the nearest chapter boundary.",
    },
    AdjustIntroBasedOnSilence: {
        kind: "checkbox",
        label: "Enable silence detection",
        description: "When enabled, segment endpoints will be adjusted to the nearest silence point.",
    },
    SilenceDetectionMaximumNoise: {
        kind: "number",
        label: "Noise tolerance",
        min: -90,
        max: 0,
        description: "Noise tolerance in negative decibels.",
    },
    SilenceDetectionMinimumDuration: {
        kind: "number",
        label: "Minimum silence duration",
        min: 0,
        step: 0.01,
        description: "Minimum silence duration in seconds before adjusting introduction end time.",
    },
    AdjustWindowInward: {
        kind: "number",
        label: "Adjustment window (inward)",
        min: 0,
        description:
            "Maximum number of seconds to look inside a detected segment for a better start or end point. A larger value can trim extra time from the beginning or end.",
    },
    AdjustWindowOutward: {
        kind: "number",
        label: "Adjustment window (outward)",
        min: 0,
        description:
            "Maximum number of seconds to look beyond a detected segment for a better start or end point. A larger value can include more time before or after the segment.",
    },
    EndSnapThreshold: {
        kind: "number",
        label: "Snap to episode start/end threshold",
        min: 0,
        description:
            "If a segment's start or end is within this many seconds of the episode's start or end, it will be automatically adjusted (snapped) to match the episode boundary. Set to 0 to disable snapping.",
    },
    FirstEpisodeIntroMode: {
        kind: "select",
        label: "First episode intro handling (per season)",
        options: [
            { value: "Analyze", label: "Analyze first episode intros" },
            { value: "Ignore", label: "Ignore first episode intros" },
            { value: "IgnoreAnime", label: "Ignore anime first episode intros" },
        ],
        description:
            "For each season, choose whether the first episode intro is analyzed, ignored, or ignored only when the show is anime.",
    },
    AnimePreviewFromCreditsEnd: {
        kind: "checkbox",
        label: "Set post-credits scene as preview for anime",
        description:
            "When enabled and no preview was detected, create a segment from the end of the last credits block to the end of the episode. This can also be toggled for any season or show in Timestamps > Manage.",
    },
    IntroStartOffset: {
        kind: "number",
        label: "Intro Start Offset (seconds)",
        min: 0,
        step: 0.5,
        description:
            "Default: 0. Example: If set to 3, the first 3 seconds of the intro will play before skipping.",
    },
    IncludeIntroStartOffsetWhenSnapping: {
        kind: "checkbox",
        label: "Include start offset when snapping to episode start",
        description:
            "When enabled, Intro Start Offset is also applied when the detected intro start is snapped to the beginning of the episode.",
    },
    IntroEndOffset: {
        kind: "number",
        label: "Intro End Offset (seconds)",
        min: 0,
        step: 0.5,
        description:
            "Default: 0. Example: If set to 3, playback will resume 3 seconds before the end of the intro.",
    },

    // Black frame
    DetectRecapUsingBlackFrames: {
        kind: "checkbox",
        label: "Detect recap using black frames",
        description:
            "When recap chapter detection fails, mark recap from 0:00 to the latest detected black frame within the detected recap duration limits and before the intro.",
    },
    AnchorRecapToColdOpen: {
        kind: "checkbox",
        label: "Keep the cold open before a recap",
        description:
            'Start a Chromaprint recap at the black frame just before the shared "previously on" sting instead of 0:00, so a scene played before the recap is not skipped. Chapter and black-frame-only recaps still start at 0:00. With the option above also enabled, Chromaprint must run first (Prefer Chromaprint Analysis on the Analysis tab), or the black-frame recap claims the episode at 0:00 before Chromaprint sees it.',
    },
    // No tab renders this. The Black Frame tab reads it to decide which of the
    // legacy and modern toggles to show.
    UseLegacyBlackFrameAnalyzer: { kind: "checkbox", label: "Use legacy black frame analyzer" },
    RefineCreditsBoundary: {
        kind: "checkbox",
        label: "Refine credits boundary",
        description:
            "Use frame-level analysis to find the exact credits boundary. Disable for faster analysis with keyframe-only accuracy.",
    },
    DetectNonBlackCredits: {
        kind: "checkbox",
        label: "Detect card credits",
        description:
            "Also detect credits shown on a near-uniform card: text over a black, white, grey, or muted-colour background. The result is combined with the black-frame and other credits candidates, so card credits before or after a black roll are included. Vivid, highly saturated backgrounds are not covered.",
    },
    UseChapterMarkersBlackFrame: {
        kind: "checkbox",
        label: "Use chapter markers for credits detection",
        description:
            "If enabled, chapter markers will be used to identify credits segments. Tries to detect credits by looking for black frames close to chapter markers.",
    },
    BlackFrameMinimumPercentage: {
        kind: "number",
        label: "Minimum percentage of black pixels",
        min: 0,
        max: 100,
        description:
            "Minimum percentage of black pixels in a frame before it is considered a black frame. Defaults to 85.",
    },
    BlackFrameThreshold: {
        kind: "number",
        label: "Black frame threshold",
        min: 16,
        max: 255,
        description: "The threshold below which a pixel value is considered black. Defaults to 28.",
    },

    // Chapters
    UseEmbeddedChaptersForAnalysis: {
        kind: "checkbox",
        label: "Use embedded chapters for analysis",
        description:
            "Read chapters from the media file instead of Jellyfin's chapter table, so chapters other plugins add are ignored. Changing this makes the next analysis run probe each analyzed episode once and reanalyze only the episodes whose file and table hold different chapters. Each analyzed episode costs one ffprobe while it is on.",
    },
    ChapterAnalyzerIntroductionPattern: {
        kind: "regex",
        label: "Introductions",
        description: "Enter a regular expression to detect introduction chapters.",
        default: "(^|\\s)(Intro|Introduction|OP|Opening)" + PATTERN_TAIL,
    },
    ChapterAnalyzerEndCreditsPattern: {
        kind: "regex",
        label: "Credits",
        description: "Enter a regular expression to detect credits chapters.",
        default: "(^|\\s)(Credits?|ED|Ending|Outro)" + PATTERN_TAIL,
    },
    ChapterAnalyzerPreviewPattern: {
        kind: "regex",
        label: "Preview",
        description: "Enter a regular expression to detect preview chapters.",
        default:
            "(^|\\s)(Preview|PV|Sneak\\s?Peek|Coming\\s?(Up|Soon)|Next\\s+(time|on|episode)|Extra|Teaser|Trailer)" +
            PATTERN_TAIL,
    },
    ChapterAnalyzerRecapPattern: {
        kind: "regex",
        label: "Recaps",
        description: "Enter a regular expression to detect recap chapters.",
        default:
            "(^|\\s)(Re?cap|Sum{1,2}ary|Prev(ious(ly)?)?(?=$|:|\\s+on\\b)|(Last|Earlier)(?=$|:|\\s+(time|week|episode|on)\\b)|Catch[ -]up)" +
            PATTERN_TAIL,
    },
    ChapterAnalyzerCommercialPattern: {
        kind: "regex",
        label: "Commercials",
        description: "Enter a regular expression to detect commercial chapters.",
        default: "(^|\\s)(Ad(vert(isement)?)?|Commercial|Intermission)" + PATTERN_TAIL,
    },
    EnableSponsorBlockChapterDetection: {
        kind: "checkbox",
        label: "Enable SponsorBlock chapter detection",
        description:
            "Detect known SponsorBlock chapter labels in addition to the regular expressions above.",
    },

    // Subtitles
    SubtitleLanguages: {
        kind: "text",
        label: "Subtitle languages",
        placeholder: "ger, eng",
        description:
            "Comma-separated language codes to read. Empty reads all. Recap title cards are often subtitled only in translation tracks, so reading only the audio language can miss them.",
    },
    SubtitleRecapPattern: {
        kind: "regex",
        label: "Recap regex for subtitles",
        description:
            "Matches text-subtitle cues announcing a recap. Enable recap matching for a season or show in Timestamps > Manage.",
        default:
            "\\bpreviously\\s+on|^\\W*(?:zuvor\\s+bei|zuletzt\\s+bei|was\\s+bisher\\s+geschah|bisher\\s+bei|anteriormente|previamente|précédemment|wat\\s+voorafging)|episodio?\\s+precedent[ei]|^\\W*前回までは|前情提要|지난\\s*이야기\\W*$",
    },
    SubtitlePreviewPattern: {
        kind: "regex",
        label: "Preview regex for subtitles",
        description:
            "Matches text-subtitle cues announcing a preview. Enable preview matching for a season or show in Timestamps > Manage.",
        default:
            "\\b(?:here(?:'|’)?s|now)\\s+the\\s+preview\\b|^\\W*(?:\\[[^\\]]*\\]|\\([^)]*\\)|[^\\s:]+:)?\\s*next\\s+time\\b",
    },
    // FFmpeg
    PreferredAudioLanguage: {
        kind: "text",
        label: "Preferred stream audio language",
        placeholder: "eng",
        description:
            "Prefer an audio stream with this language tag when generating Chromaprint fingerprints. Empty or unmatched values will use the stream selection policy below.",
    },
    PreferAudioStreamWithMostChannels: {
        kind: "checkbox",
        label: "Prefer highest audio channel count",
        description:
            "When enabled, Chromaprint uses the stream FFmpeg would pick, ranking the default stream first, then channel count, then stream order. When disabled, the first stream is selected.",
    },
    ProbeAudioDuration: {
        kind: "checkbox",
        label: "Probe audio duration for credits",
        description:
            "Use ffprobe to base credits fingerprinting on the first audio stream duration when container runtime is longer than the audio.",
    },
    MaxParallelism: {
        kind: "number",
        label: "Maximum degree of parallelism",
        min: 1,
        description: "Maximum number of seasons analyzed concurrently; episodes within each season run sequentially.",
    },
    ProcessPriority: {
        kind: "select",
        label: "FFmpeg Priority",
        options: [
            { value: "Idle", label: "Idle" },
            { value: "BelowNormal", label: "Below Normal" },
            { value: "Normal", label: "Normal" },
            { value: "AboveNormal", label: "Above Normal" },
            { value: "High", label: "High" },
            { value: "RealTime", label: "Highest" },
        ],
        description:
            "Priority of analysis FFmpeg processes. Below Normal reduces contention; Realtime may starve other work.",
    },
    ProcessThreads: {
        kind: "number",
        label: "FFmpeg codec threads",
        min: 0,
        max: 16,
        description:
            "Codec threads per FFmpeg process. 0 lets FFmpeg choose. Lower values reduce CPU use but may slow scans; use Maximum degree of parallelism to limit process count.",
    },
    ScanTimeoutSeconds: {
        kind: "number",
        label: "FFmpeg scan timeout (seconds)",
        min: 0,
        description:
            "Kill a fingerprint or detection scan that runs longer than this. Raise it for high-bitrate media on slow disks. 0 disables the limit.",
    },
    CacheCompressionLevel: {
        kind: "select",
        label: "Cache Compression Level",
        options: [
            { value: "NoCompression", label: "No Compression" },
            { value: "Fastest", label: "Fastest" },
            { value: "Optimal", label: "Optimal" },
            { value: "SmallestSize", label: "Smallest Size" },
        ],
        description:
            "Controls the Brotli compression level for the detection cache. " +
            "Higher compression reduces disk usage but increases CPU time during analysis. " +
            "Changing this only affects newly cached data.",
    },
} as const satisfies FieldSchema;

export type ConfigSchema = typeof configSchema;
export type ConfigKey = keyof ConfigSchema;

/**
 * The plugin configuration as the server sends it. Every schema key is
 * editable. The rest are only read: FileTransformationPluginEnabled is set by
 * the server, and the subtitle detection values are what a season without its
 * own subtitle override inherits.
 */
export type PluginConfig = ValuesOf<ConfigSchema> & {
    readonly FileTransformationPluginEnabled: boolean;
    readonly EnableSubtitleRecapDetection: boolean;
    readonly EnableSubtitlePreviewDetection: boolean;
};

/** Config keys whose value has type V, so a control binds only to keys it can hold. */
export type ConfigKeysOfType<V> = {
    [K in ConfigKey]: PluginConfig[K] extends V ? K : never;
}[ConfigKey];

/** Config keys whose schema entry has the given kind. */
export type KeyOfKind<Kd extends FieldKind> = KeysOfKind<ConfigSchema, Kd>;

export const configKeys = Object.keys(configSchema) as ConfigKey[];

export function isKind<Kd extends FieldKind>(key: ConfigKey, kind: Kd): key is KeyOfKind<Kd> {
    return configSchema[key].kind === kind;
}

/** Min/max field pairs that must stay ordered; validation flags both sides. */
export const orderedPairs = [
    ["MinimumIntroDuration", "MaximumIntroDuration"],
    ["MinimumCreditsDuration", "MaximumCreditsDuration"],
    ["MinimumRecapDuration", "MaximumRecapDuration"],
    ["MinimumRecapDetectionDuration", "MaximumRecapDetectionDuration"],
    ["MinimumPreviewDuration", "MaximumPreviewDuration"],
    ["MinimumCommercialDuration", "MaximumCommercialDuration"],
] as const satisfies readonly (readonly [ConfigKeysOfType<number>, ConfigKeysOfType<number>])[];
