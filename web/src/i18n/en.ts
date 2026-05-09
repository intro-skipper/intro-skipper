/**
 * English (default) locale strings for the Intro Skipper dashboard.
 *
 * Keys use the format `<section>_<name>` where section is a lowercase
 * abbreviation of the component or tab (e.g. `shell_`, `actionBar_`,
 * `general_`, …).
 *
 * Strings may contain HTML markup (those that are rendered via
 * element.innerHTML) — translators must preserve the surrounding HTML
 * structure and only translate the human-readable text nodes.
 *
 * Interpolation placeholders are written as `{name}` and are replaced at
 * runtime by the `t()` function.
 */
export const en = {
    // ─── App shell ─────────────────────────────────────────────────────────────
    shell_skipToContent: "Skip to content",
    shell_title: "Intro Skipper Configuration",
    shell_settingsSections: "Settings Sections",
    shell_saveControls: "Save controls",
    shell_unsavedChanges: "\u25cf Unsaved changes",
    shell_saveAriaLabel: "Save configuration",
    shell_save: "Save",
    shell_saving: "Saving\u2026",
    shell_changesSaved: "Changes saved",
    shell_saveFailed: "Save failed",
    shell_failedToSaveConfig: "Failed to save configuration",
    shell_validationWarning: "There are validation warnings. Save anyway?",
    shell_validationTitle: "Validation",

    // ─── Tab labels ────────────────────────────────────────────────────────────
    tab_general: "General",
    tab_analysis: "Analysis",
    tab_detection: "Detection",
    tab_blackFrame: "Black Frame",
    tab_chapters: "Chapters",
    tab_ffmpeg: "FFmpeg",
    tab_timestamps: "Timestamps",
    tab_tools: "Tools",
    tab_information: "Information",

    // ─── General tab ───────────────────────────────────────────────────────────
    general_injectCssTitle: "Inject Skip Button CSS",
    general_injectCssDesc:
        "Inject CSS to load skip button styles into your Jellyfin branding setting using an @import statement.",
    general_injectCssButton: "Inject CSS",
    general_injectingCss: "Injecting CSS\u2026",
    general_injectCssSuccess: "Skip button CSS injected successfully!",
    general_injectCssFailedStatus: "Failed to inject CSS: Server returned {status}",
    general_injectCssFailedMsg: "Failed to inject CSS: {msg}",
    general_ftWarning:
        "<strong>File Transformation Plugin Required</strong><br/>" +
        "This feature requires the File Transformation plugin to work. " +
        '<a href="https://github.com/IAmParadox27/jellyfin-plugin-file-transformation" target="_blank">Install it here</a>',
    general_autoAnalyzeLabel: "Automatically Analyze New Media",
    general_autoAnalyzeDesc:
        'If enabled, new media will be automatically analyzed for skippable segments when added to the library<br/><br/>Note: To configure the scheduled task, see <a is="emby-linkbutton" class="button-link" href="#/dashboard/tasks">scheduled tasks</a>.',
    general_updateSegmentsLabel: "Update Missing Segments During Scan",
    general_updateSegmentsDesc:
        "Enable this option to update media segments for any uncached media during a library scan.<br/>This includes recently added, modified, or previously skipped (but not ignored) files.<br/><b>Warning:</b> This should be disabled if you're using media segment providers other than Intro Skipper.",
    general_excludeSeriesLabel: "Exclude series",
    general_excludeSeriesDesc:
        "Exclude series from analysis. Enter a comma-separated list of series names to exclude.",
    general_analyzeForLabel: "Analyze for:",
    general_segIntroduction: "Introduction",
    general_segCredits: "Credits",
    general_segRecap: "Recap",
    general_segPreview: "Preview",
    general_segCommercials: "Commercials",
    general_analyzeSeasonZeroLabel: "Analyze Season 0 (Specials / Extras)",
    general_analyzeSeasonZeroDesc:
        "Note: Shows containing both a specials and extra folder will identify extras as season 0 and ignore specials, regardless of this setting.",
    general_useFileTransformLabel:
        "Use File Transformation Plugin to patch the web interface",
    general_skipButtonDelayLabel: "Skip button hide delay (in seconds)",
    general_skipButtonDelayDesc:
        "Time in seconds before the skip button automatically hides. Set to 0 for persistent skip button (never hides).",
    general_skipButtonDelayWarning:
        "Note: This setting only applies to the web client (browsers, LG webOS, Android with web player enabled, etc). May require a refresh or clearing cache to see changes.",
    general_enableMainMenuLabel: "Show Intro Skipper in Main Menu",
    general_enableMainMenuDesc:
        "Toggle the Intro Skipper entry in the server's main navigation. Save and refresh the client (or clear cache) to apply.",

    // ─── Analysis tab ──────────────────────────────────────────────────────────
    analysis_warning:
        "Changes here require regenerating media segments to take effect. Per the MediaSegments API, records are updated individually and may be slow.",
    analysis_info:
        "<p>The amount of each item\u2019s content that will be analyzed is determined using the percentage and maximum runtime. The minimum of (duration \u00d7 percent, maximum runtime) is the amount that will be analyzed.</p>" +
        "<p>If the percentage or maximum runtime settings are modified, the cached fingerprints and timestamps for each series, season, or movie you want to analyze with the modified settings <b>will have to be recreated</b>.</p>" +
        "<p>Increasing either of the above settings will cause episode analysis to take much longer.</p>",
    analysis_preferChromaprintLabel: "Prefer Chromaprint Analysis",
    analysis_preferChromaprintDesc:
        "Only use chromaprint for analysis, unless it is not available. Setting an analysis mode in the advanced options will override this setting.",
    analysis_fullLengthChaptersLabel: "Ignore duration limits for chapters",
    analysis_fullLengthChaptersDesc:
        "Allow segments to extend to the end of a chapter when the marker exceeds other user settings, such as percentage or duration.",
    analysis_percentLabel: "Percent of media to analyze",
    analysis_percentDesc:
        "Analysis will be limited to this percentage of each item's runtime. For example, a value of 25 (the default) will limit analysis to the first quarter of each item.",
    analysis_maxRuntimeLabel: "Maximum runtime to analyze (in minutes)",
    analysis_maxRuntimeDesc:
        "Analysis will be limited to this amount of each item's runtime. For example, a value of 10 (the default) will limit analysis to the first 10 minutes of each item.",
    analysis_minIntroDurationLabel: "Minimum introduction duration (in seconds)",
    analysis_maxIntroDurationLabel: "Maximum introduction duration (in seconds)",
    analysis_minIntroDurationDesc:
        "Segments or similar sounding audio which is shorter than this duration will not be considered an introduction.",
    analysis_maxIntroDurationDesc:
        "Segments or similar sounding audio which is longer than this duration will not be considered an introduction.",
    analysis_minCreditsDurationLabel: "Minimum credits duration (in seconds)",
    analysis_maxCreditsDurationLabel: "Maximum credits duration (in seconds)",
    analysis_minCreditsDurationDesc:
        "Segments or similar sounding audio which is shorter than this duration will not be considered credits.",
    analysis_maxCreditsDurationDesc:
        "Segments or similar sounding audio which is longer than this duration will not be considered credits.",
    analysis_maxMovieCreditsDurationLabel: "Maximum movie credits duration (in seconds)",
    analysis_maxMovieCreditsDurationDesc:
        "Segments longer than this duration will not be considered movie credits.",
    analysis_minRecapDurationLabel: "Minimum recap duration (in seconds)",
    analysis_maxRecapDurationLabel: "Maximum recap duration (in seconds)",
    analysis_minRecapDurationDesc:
        "Segments which are shorter than this duration will not be considered a recap.",
    analysis_maxRecapDurationDesc:
        "Segments which are longer than this duration will not be considered a recap.",
    analysis_minPreviewDurationLabel: "Minimum preview duration (in seconds)",
    analysis_maxPreviewDurationLabel: "Maximum preview duration (in seconds)",
    analysis_minPreviewDurationDesc:
        "Segments which are shorter than this duration will not be considered a preview.",
    analysis_maxPreviewDurationDesc:
        "Segments which are longer than this duration will not be considered a preview.",
    analysis_minCommercialDurationLabel: "Minimum commercial duration (in seconds)",
    analysis_maxCommercialDurationLabel: "Maximum commercial duration (in seconds)",
    analysis_minCommercialDurationDesc:
        "Segments which are shorter than this duration will not be considered a commercial.",
    analysis_maxCommercialDurationDesc:
        "Segments which are longer than this duration will not be considered a commercial.",

    // ─── Detection tab ─────────────────────────────────────────────────────────
    detection_silenceLabel: "Enable silence detection",
    detection_silenceDesc:
        "When enabled, segment endpoints will be adjusted to the nearest silence point.",
    detection_noiseLabel: "Noise tolerance",
    detection_noiseDesc: "Noise tolerance in negative decibels.",
    detection_minSilenceLabel: "Minimum silence duration",
    detection_minSilenceDesc:
        "Minimum silence duration in seconds before adjusting introduction end time.",
    detection_keyframeLabel: "Enable keyframe snapping",
    detection_keyframeDesc:
        "When enabled, segment endpoints will be adjusted to the nearest video keyframe for smoother seek transitions during skipping.",
    detection_chapterSnapLabel: "Enable chapter snapping",
    detection_chapterSnapDesc:
        "When enabled, segment start and end times will be adjusted to the nearest chapter boundary.",
    detection_adjustWindowInwardLabel: "Adjustment window (inward)",
    detection_adjustWindowInwardDesc:
        "Maximum number of seconds to search toward a segment\u2019s interior for adjustment points (like chapter boundaries, silence, or keyframes). Used to tighten segment boundaries.",
    detection_adjustWindowOutwardLabel: "Adjustment window (outward)",
    detection_adjustWindowOutwardDesc:
        "Maximum number of seconds to search away from a segment for adjustment points (like chapter boundaries, silence, or keyframes). Used to expand segment boundaries.",
    detection_endSnapThresholdLabel: "Snap to episode start/end threshold",
    detection_endSnapThresholdDesc:
        "If a segment's start or end is within this many seconds of the episode's start or end, it will be automatically adjusted (snapped) to match the episode boundary. Set to 0 to disable snapping.",
    detection_skipFirstEpisodeLabel: "Ignore intros for first episode of a season",
    detection_skipFirstAnimeLabel: "Only ignore first episode of an anime season",
    detection_skipFirstAnimeDesc:
        "If checked, the previous ignore option will only be applied to anime seasons.",
    detection_animePreviewLabel: "Set after credits scene as preview for anime",
    detection_animePreviewDesc:
        "When enabled, a preview segment covering the time from the end of the credits to the end of the episode is created for anime without a detected preview.",
    detection_segmentOffsetTitle: "Segment Offset Adjustment",
    detection_introStartOffsetLabel: "Intro Start Offset (seconds)",
    detection_introStartOffsetDesc:
        "Default: 0. Example: If set to 3, the first 3 seconds of the intro will play before skipping.",
    detection_introEndOffsetLabel: "Intro End Offset (seconds)",
    detection_introEndOffsetDesc:
        "Default: 0. Example: If set to 3, playback will resume 3 seconds before the end of the intro.",

    // ─── Black Frame tab ───────────────────────────────────────────────────────
    blackFrame_altAnalyzerLabel: "Use alternative black frame analyzer (experimental)",
    blackFrame_altAnalyzerDesc:
        "If enabled, the alternative black frame analyzer will be used. This analyzer is experimental and may not work as expected.",
    blackFrame_refineBoundaryLabel: "Refine credits boundary",
    blackFrame_refineBoundaryDesc:
        "Use frame-level analysis to find the exact credits boundary. Disable for faster analysis with keyframe-only accuracy.",
    blackFrame_useChapterMarkersLabel: "Use chapter markers for credits detection",
    blackFrame_useChapterMarkersDesc:
        "If enabled, chapter markers will be used to identify credits segments. Tries to detect credits by looking for black frames close to chapter markers.",
    blackFrame_minPercentageLabel: "Minimum percentage of black pixels",
    blackFrame_minPercentageDesc:
        "Minimum percentage of black pixels in a frame before it is considered a black frame. Defaults to 85.",
    blackFrame_thresholdLabel: "Black frame threshold",
    blackFrame_thresholdDesc:
        "The threshold below which a pixel value is considered black. Defaults to 32.",

    // ─── Chapters tab ──────────────────────────────────────────────────────────
    chapters_introductionsLabel: "Introductions",
    chapters_creditsLabel: "Credits",
    chapters_previewLabel: "Preview",
    chapters_recapsLabel: "Recaps",
    chapters_commercialsLabel: "Commercials",
    chapters_introductionNoun: "introduction",
    chapters_creditsNoun: "credits",
    chapters_previewNoun: "preview",
    chapters_recapNoun: "recap",
    chapters_commercialNoun: "commercial",
    chapters_resetButton: "Reset to default",
    chapters_patternDesc:
        "Enter a regular expression to detect {typeNoun} chapters. <br/>Default: <code>{defaultPattern}</code>",

    // ─── FFmpeg tab ────────────────────────────────────────────────────────────
    ffmpeg_maxParallelismLabel: "Maximum degree of parallelism",
    ffmpeg_maxParallelismDesc:
        "Maximum number of simultaneous async episode analysis operations.",
    ffmpeg_priorityLabel: "FFmpeg Priority",
    ffmpeg_priorityDesc:
        "Sets the relative priority of the analysis FFmpeg process to other parallel operations.",
    ffmpeg_priorityIdle: "Idle",
    ffmpeg_priorityBelowNormal: "Below Normal",
    ffmpeg_priorityNormal: "Normal",
    ffmpeg_priorityAboveNormal: "Above Normal",
    ffmpeg_priorityHigh: "High",
    ffmpeg_priorityHighest: "Highest",
    ffmpeg_threadsLabel: "FFmpeg Threads",
    ffmpeg_threadsDesc:
        "Number of simultaneous processes to use for FFmpeg operations. Setting 0 (default) uses the maximum threads available.",
    ffmpeg_cacheLabel: "Cache Compression Level",
    ffmpeg_cacheDesc:
        "Controls the Brotli compression level for the detection cache. " +
        "Higher compression reduces disk usage but increases CPU time during analysis. " +
        "Changing this only affects newly cached data.",
    ffmpeg_cacheNoCompression: "No Compression",
    ffmpeg_cacheFastest: "Fastest",
    ffmpeg_cacheOptimal: "Optimal",
    ffmpeg_cacheSmallestSize: "Smallest Size",

    // ─── Timestamps browser ────────────────────────────────────────────────────
    timestamps_allLibraries: "All Libraries",
    timestamps_loadingItems: "Loading items\u2026",
    timestamps_unavailable: "Unavailable",
    timestamps_failedToLoadLibraries: "Failed to load libraries: {error}",
    timestamps_loadingShows: "Loading shows\u2026",
    timestamps_failedToLoadShows: "Failed to load shows: {error}",
    timestamps_noShowsFound: "No shows found in this library.",
    timestamps_failedToLoadSeasons: "Failed to load seasons: {error}",
    timestamps_noSeasonsFound: "No seasons found.",
    timestamps_loadingEpisodes: "Loading episodes\u2026",
    timestamps_noEpisodesFound: "No episodes found.",
    timestamps_failedToLoadEpisodes: "Failed to load episodes: {error}",
    timestamps_loadingTimestamps: "Loading timestamps\u2026",
    timestamps_failedToLoadTimestamps: "Failed to load timestamps: {error}",

    // ─── Tools tab ─────────────────────────────────────────────────────────────
    tools_globalTimestampTypeLabel: "Global Timestamp Type",
    tools_segIntroduction: "Introduction",
    tools_segRecap: "Recap",
    tools_segCredits: "Credits",
    tools_segPreview: "Preview",
    tools_segCommercial: "Commercial",
    tools_eraseAllButton: "Erase All {type} Timestamps",
    tools_eraseDialogTitle: "Confirm Timestamp Erasure",
    tools_eraseDialogBody:
        "Are you sure you want to erase all previously discovered {type} timestamps?",
    tools_eraseConfirmLabel: "Erase",
    tools_eraseIncludeFingerprints: "Include cached fingerprint files",
    tools_eraseFailed: "Failed to erase {type} timestamps",
    tools_eraseSuccess: "{type} timestamps erased",
    tools_rebuildButton: "Rebuild Local Database",
    tools_rebuildDialogTitle: "Confirm Database Rebuild",
    tools_rebuildDialogBody:
        "Are you sure you want to rebuild the database? This requires a full Jellyfin restart to complete.",
    tools_rebuildConfirmLabel: "Rebuild",
    tools_rebuildFailed: "Failed to rebuild database",
    tools_rebuildSuccess:
        "Database rebuild initiated. A full Jellyfin restart is required.",
    tools_rebuildWarning:
        "Rebuilding the database requires a full Jellyfin restart to complete, not just a dashboard restart.",

    // ─── Information tab ───────────────────────────────────────────────────────
    information_supportTitle: "Intro Skipper Support Log",
    information_supportLoadingText: "Loading support log\u2026",
    information_supportLoadedText: "Support log loaded.",
    information_supportEmptyText: "Support log is empty.",
    information_supportErrorText: "Failed to load support log.",
    information_copyButton: "Copy to Clipboard",
    information_copySuccess: "Support bundle copied to clipboard",
    information_copyFallback: "Press Ctrl+C to copy support bundle",
    information_storageTitle: "Storage Usage",
    information_storageDesc: "See how much space each library uses.",
    information_storageLoadingText: "Loading storage usage\u2026",
    information_storageEmptyText: "Storage usage is empty.",
    information_storageLoadedText: "Storage usage loaded.",
    information_storageErrorText: "Failed to load storage usage.",

    // ─── Action bar ────────────────────────────────────────────────────────────
    actionBar_recapLabel: "Recap",
    actionBar_introLabel: "Intro",
    actionBar_creditsLabel: "Credits",
    actionBar_previewLabel: "Preview",
    actionBar_commercialLabel: "Commercial",
    actionBar_optionDefault: "Default",
    actionBar_optionChapter: "Chapter",
    actionBar_optionChromaprint: "Chromaprint",
    actionBar_optionBlackFrame: "BlackFrame",
    actionBar_optionNone: "None",
    actionBar_saveOverridesButton: "Save Analyzer Overrides",
    actionBar_scanSeasonButton: "Scan Season",
    actionBar_scanMovieButton: "Scan Movie",
    actionBar_eraseSeasonButton: "Erase Season Timestamps",
    actionBar_eraseMovieButton: "Erase Movie Timestamps",
    actionBar_savingOverrides: "Saving analyzer overrides\u2026",
    actionBar_overridesSaved: "Analyzer overrides updated.",
    actionBar_overridesFailed: "Failed to update analyzer overrides.",
    actionBar_analyzerActionsUpdated: "Analyzer actions updated",
    actionBar_analyzerActionsFailed: "Failed to update analyzer actions",
    actionBar_scanFinished: "Scan finished. Results refreshed.",
    actionBar_scanPollingTimeout:
        "Scan status polling timed out. Refresh to check results.",
    actionBar_scanAlreadyRunning: "A scan is already in progress.",
    actionBar_scanUnableToStart: "Unable to start the scan.",
    actionBar_scanInProgress: "Scan in progress\u2026 This can take several minutes.",
    actionBar_startingScan: "Starting scan\u2026",
    actionBar_scanInProgressButton: "Scan in progress\u2026",
    actionBar_eraseDialogTitle: "Confirm Timestamp Erasure",
    actionBar_eraseDialogBody:
        "Are you sure you want to erase all timestamps for this {label}?",
    actionBar_eraseConfirmLabel: "Erase",
    actionBar_eraseIncludeFingerprints: "Include cached fingerprints",
    actionBar_erasingTimestamps: "Erasing timestamps\u2026",
    actionBar_eraseTimestampsFailed: "Failed to erase timestamps.",
    actionBar_eraseTimestampsSuccess: "Timestamps erased.",
    actionBar_eraseTimestampsFailedAlert: "Failed to erase timestamps",
    actionBar_eraseTimestampsSuccessAlert: "Timestamps erased",
    actionBar_segmentEditorLink: "Segment Editor \u2192",
    actionBar_seasonLabel: "season",
    actionBar_movieLabel: "movie",

    // ─── Episode list ──────────────────────────────────────────────────────────
    epList_filterPlaceholder: "Filter episodes\u2026",
    epList_filterAriaLabel: "Filter episodes by name",
    epList_failedToLoadTimestamps: "Failed to load timestamps",
    epList_retry: "Retry",
    epList_retryAriaLabel: "Retry loading timestamps for {name}",
    epList_loading: "Loading\u2026",
    epList_noMatchingEpisodes: "No matching episodes",
    epList_oneEpisode: "1 episode",
    epList_manyEpisodes: "{count} episodes",
    epList_noEpisodesFound: "No episodes found.",

    // ─── Manage bar ────────────────────────────────────────────────────────────
    manageBar_manage: "\u2699 Manage",
    manageBar_toggleAriaLabel: "Toggle management panel",

    // ─── Breadcrumb nav ────────────────────────────────────────────────────────
    breadcrumb_navLabel: "Breadcrumb",
    breadcrumb_searchPlaceholder: "Search all shows\u2026",
    breadcrumb_searchAriaLabel: "Search all shows",
    breadcrumb_searchResultsLabel: "Search results",
    breadcrumb_noShowsFound: "No shows found.",

    // ─── Confirm dialog ────────────────────────────────────────────────────────
    dialog_cancel: "Cancel",
    dialog_confirm: "Confirm",

    // ─── Timestamp nav (library item count) ────────────────────────────────────
    nav_oneItem: "1 item",
    nav_manyItems: "{count} items",
    // ─── Validation messages ────────────────────────────────────────────────────
    validation_invalidRegex: "Invalid regular expression",
    validation_mustBeBetween: "Must be between {min} and {max}",
    validation_mustBeAtLeast: "Must be at least {min}",
    validation_mustBeLessThanMax: "Must be less than maximum",
    validation_mustBeGreaterThanMin: "Must be greater than minimum",
} as const;
