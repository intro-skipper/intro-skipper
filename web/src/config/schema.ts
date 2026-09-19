// The one declaration of every plugin setting the dashboard edits. The
// PluginConfig type, the validator, the form controls and the tabs all read
// from here. Which tab shows a field, and in what order, is the tab's own
// business; a field's visibility rule stays with the tab too, because it needs
// the config store.

type SelectOption = { readonly value: string; readonly label: string };

type FieldBase = {
    readonly label: string;
    /** Static HTML. Never built from user input. */
    readonly description?: string;
    /** Static HTML. Never built from user input. */
    readonly warning?: string;
};

export type FieldSpec =
    | (FieldBase & { readonly kind: "checkbox" })
    | (FieldBase & {
          readonly kind: "number";
          /** Doubles as the HTML min attribute and the validation floor. */
          readonly min?: number;
          /** Doubles as the HTML max attribute and the validation ceiling. */
          readonly max?: number;
          readonly step?: number;
      })
    | (FieldBase & { readonly kind: "text"; readonly placeholder?: string })
    /** A regular expression with a reset-to-default; the default is also the placeholder. */
    | (FieldBase & { readonly kind: "regex"; readonly default: string })
    | (FieldBase & { readonly kind: "select"; readonly options: readonly SelectOption[] })
    /** A list of trimmed, non-empty strings. */
    | (FieldBase & { readonly kind: "list"; readonly placeholder?: string });

export type FieldKind = FieldSpec["kind"];

const PATTERN_TAIL = "(?![\\s:]+End)(\\s|:|$)";

export const configSchema = {
    // General
    AutoDetectIntros: {
        kind: "checkbox",
        label: "Automatically Analyze New Media",
        description:
            'If enabled, new media will be automatically analyzed for skippable segments when added to the library<br/><br/>Note: To configure the scheduled task, see <a is="emby-linkbutton" class="button-link" href="#/dashboard/tasks">scheduled tasks</a>.',
    },
    ReanalyzeSettledSeasons: {
        kind: "checkbox",
        label: "Re-analyze settled seasons",
        description:
            "When a season has no new episodes for the configured delay, re-analyze the whole season so segments first detected from only a few episodes are recomputed against the full season. Uses cached fingerprints, so it does not re-decode media.",
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
            "Enable this option to update media segments for any uncached media during a library scan.<br/>This includes recently added, modified, or previously skipped (but not ignored) files.<br/><b>Warning:</b> This should be disabled if you're using media segment providers other than Intro Skipper.",
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
    EnableChapterAnalyzer: {
        kind: "checkbox",
        label: "Chapter analysis",
        description:
            "Match chapter names against the configured patterns. Used by every segment type.",
        warning:
            "Turning this off re-analyzes <b>every</b> segment type, recomputing their stored timestamps without chapter matches. Snapping a boundary to a chapter is a separate setting on the Detection tab and is unaffected.",
    },
    EnableChromaprintAnalyzer: {
        kind: "checkbox",
        label: "Chromaprint fingerprinting",
        description:
            "Compare the episodes of a season by audio fingerprint. Used by Introduction, Recap and Credits. This is the slowest method; turning it off skips fingerprint decoding entirely.",
        warning:
            "Turning this off re-analyzes Introduction, Recap and Credits, recomputing their stored timestamps without fingerprint matches. Cached fingerprints are kept and reused if you turn it back on.",
    },
    EnableKeyframeAnalyzer: {
        kind: "checkbox",
        label: "Keyframe scan",
        description:
            "Decode keyframes for black-frame evidence and keyframe visuals. Used by Credits, by the recap black-frame fallback on the Black Frame tab, and to place the end of a Chromaprint recap.",
        warning:
            "Turning this off re-analyzes Credits and Recap, recomputing their stored timestamps without black-frame evidence. Recap detection is then left to chapters: a Chromaprint recap has no way to place its end without this scan. Cached scans are kept and reused if you turn it back on.",
    },
    EnhanceChapterCredits: {
        kind: "checkbox",
        label: "Enhance chapter credits",
        description:
            "Combine recognized credits chapters with black-frame and chromaprint results, which can extend the credits or find additional blocks. Off, a recognized credits chapter is used as authored, even when offsets leave no skippable range. Changing this re-analyzes credits on the next scan; existing timestamps stay until then.",
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
    AdjustWindowInward: {
        kind: "number",
        label: "Adjustment window (inward)",
        min: 0,
        description:
            "Maximum number of seconds to search toward a segment's interior for adjustment points (like chapter boundaries, silence, or keyframes). Used to tighten segment boundaries.",
    },
    AdjustWindowOutward: {
        kind: "number",
        label: "Adjustment window (outward)",
        min: 0,
        description:
            "Maximum number of seconds to search away from a segment for adjustment points (like chapter boundaries, silence, or keyframes). Used to expand segment boundaries.",
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
        label: "Set after credits scene as preview for anime",
        description:
            "When enabled, a preview segment is created for anime without a detected preview, from the end of the credits to the next credits block or the end of the episode. This can also be toggled for any season or show in Timestamps > Manage.",
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
            "(^|\\s)(Re?cap|Sum{1,2}ary|Prev(ious(ly)?)?|(Last|Earlier)(\\s\\w+)?|Catch[ -]up)" +
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

    // FFmpeg
    MaxParallelism: {
        kind: "number",
        label: "Maximum degree of parallelism",
        min: 1,
        description: "Maximum number of simultaneous async episode analysis operations.",
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
            "Sets the relative priority of the analysis FFmpeg process to other parallel operations.",
    },
    ProcessThreads: {
        kind: "number",
        label: "FFmpeg Threads",
        min: 0,
        max: 16,
        description:
            "Number of simultaneous processes to use for FFmpeg operations. Setting 0 (default) uses the maximum threads available.",
    },
    ScanTimeoutSeconds: {
        kind: "number",
        label: "FFmpeg scan timeout (seconds)",
        min: 0,
        description:
            "Kill a fingerprint or detection scan that runs longer than this. Raise it for high-bitrate media on slow disks. 0 disables the limit.",
    },
    ProbeAudioDuration: {
        kind: "checkbox",
        label: "Probe audio duration for credits",
        description:
            "Use ffprobe to base credits fingerprinting on the first audio stream duration when container runtime is longer than the audio.",
    },
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
            "When enabled, Chromaprint selects streams based on channel count, using the lowest index as the tie breaker. When disabled, the first stream is selected.",
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
} as const satisfies Record<string, FieldSpec>;

export type ConfigSchema = typeof configSchema;
export type ConfigKey = keyof ConfigSchema;

type ValueOf<S extends FieldSpec> = S extends { kind: "checkbox" }
    ? boolean
    : S extends { kind: "number" }
      ? number
      : S extends { kind: "select"; options: readonly { value: infer V }[] }
        ? V
        : S extends { kind: "text" | "regex" }
          ? string
          : S extends { kind: "list" }
            ? string[]
            : never;

/**
 * The plugin configuration as the server sends it. Every schema key is
 * editable; FileTransformationPluginEnabled is set by the server and only read.
 */
export type PluginConfig = { -readonly [K in ConfigKey]: ValueOf<ConfigSchema[K]> } & {
    readonly FileTransformationPluginEnabled: boolean;
};

/** Config keys whose value has type V, so a control binds only to keys it can hold. */
export type ConfigKeysOfType<V> = {
    [K in ConfigKey]: PluginConfig[K] extends V ? K : never;
}[ConfigKey];

/** Config keys whose schema entry has the given kind. */
export type KeyOfKind<Kd extends FieldKind> = {
    [K in ConfigKey]: ConfigSchema[K] extends { kind: Kd } ? K : never;
}[ConfigKey];

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
