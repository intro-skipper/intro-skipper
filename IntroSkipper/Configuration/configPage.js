// ========== GLOBAL STATE ========== //
let lhs = [];
let rhs = [];
let fprDiffs = [];
const fprDiffMinimum = (1 - 6 / 32) * 100;
let shows = {};
let windowHashInterval = 0;

// ========== DOM ELEMENTS ========== //
const visualizer = document.querySelector("details#visualizer");
const support = document.querySelector("details#support");
const storage = document.querySelector("details#storage");
const btnRebuildDatabase = document.querySelector("button#btnRebuildDatabase");
const analyzeMovies = document.getElementById("AnalyzeMovies");
const analyzerActionsSection = document.querySelector("div#analyzerActionsSection");
const actionIntro = analyzerActionsSection.querySelector("select#actionIntro");
const actionCredits = analyzerActionsSection.querySelector("select#actionCredits");
const actionRecap = analyzerActionsSection.querySelector("select#actionRecap");
const actionPreview = analyzerActionsSection.querySelector("select#actionPreview");
const saveAnalyzerActionsButton = analyzerActionsSection.querySelector("button#saveAnalyzerActions");
const scanSeasonButton = document.querySelector("button#scanSeason");
const canvas = document.querySelector("canvas#troubleshooter");
const selectShow = document.querySelector("select#troubleshooterShow");
const seasonSelection = document.getElementById("seasonSelection");
const selectSeason = document.querySelector("select#troubleshooterSeason");
const episodeSelection = document.getElementById("episodeSelection");
const selectEpisode1 = document.querySelector("select#troubleshooterEpisode1");
const selectEpisode2 = document.querySelector("select#troubleshooterEpisode2");
const txtOffset = document.querySelector("input#offset");
const txtSuggested = document.querySelector("span#suggestedShifts");
const btnSeasonEraseTimestamps = document.querySelector("button#btnEraseSeasonTimestamps");
const eraseSeasonContainer = document.getElementById("eraseSeasonContainer");
const btnMovieEraseTimestamps = document.querySelector("button#btnEraseMovieTimestamps");
const eraseMovieContainer = document.getElementById("eraseMovieContainer");
const timestampError = document.querySelector("textarea#timestampError");
const timestampEditor = document.querySelector("#timestampEditor");
const rightEpisodeEditor = document.getElementById("rightEpisodeEditor");
const btnUpdateTimestamps = document.querySelector("button#btnUpdateTimestamps");
const timeContainer = document.querySelector("span#timestampContainer");
const fingerprintVisualizer = document.getElementById("fingerprintVisualizer");
const silenceSettings = document.getElementById("silenceSettings");
const pluginSkip = document.getElementById("PluginSkip");
const serverSkipSettings = document.getElementById("ServerSkipSettings");
const autoSkip = document.getElementById("AutoSkip");
const selectAllLibraries = document.querySelector("input#SelectAllLibraries");
const librariesContainer = document.querySelector("div.folderAccessListContainer");
const autoSkipClientList = document.querySelector("div.AutoSkipClientListContainer");
const fullLengthChapters = document.getElementById("FullLengthChapters");
const useAlternativeBlackFrameAnalyzer = document.getElementById("UseAlternativeBlackFrameAnalyzer");
const chapterMarkersBlackFrameSetting = document.getElementById("ChapterMarkersBlackFrameSetting");
const snapToKeyframe = document.getElementById("SnapToKeyframe");
const adjustIntroBasedOnSilence = document.getElementById("AdjustIntroBasedOnSilence");
const adjustIntroBasedOnChapters = document.getElementById("AdjustIntroBasedOnChapters");
const globalTimestamps = document.getElementById("GlobalTimestamps");
const btnEraseGlobalTimestamps = document.getElementById("btnEraseGlobalTimestamps");
const skipFirstEpisode = document.getElementById("divSkipFirstEpisode");
const IntroStartOffset = document.getElementById("IntroStartOffset");
const AutoSkipDelay = document.getElementById("AutoSkipDelay");
const recapPreviewDurations = document.getElementById("RecapPreviewDurations");

// ========== CONFIGURATION FIELDS ========== //
const configurationFields = [
    "MaxParallelism", "SelectedLibraries", "AdjustWindowInward", "AdjustWindowOutward", "EndSnapThreshold", "ExcludeSeries", "ClientList", "AnalysisPercent", "AnalysisLengthLimit", "MinimumIntroDuration", "MaximumIntroDuration", "MinimumCreditsDuration", "MaximumCreditsDuration", "MaximumMovieCreditsDuration", "MinimumRecapDuration", "MaximumRecapDuration", "MinimumPreviewDuration", "MaximumPreviewDuration", "ProcessPriority", "ProcessThreads", "IntroEndOffset", "IntroStartOffset", "AutoSkipDelay", "SilenceDetectionMaximumNoise", "SilenceDetectionMinimumDuration", "BlackFrameMinimumPercentage", "BlackFrameThreshold", "ChapterAnalyzerIntroductionPattern", "ChapterAnalyzerEndCreditsPattern", "ChapterAnalyzerPreviewPattern", "ChapterAnalyzerRecapPattern", "TypeList", "AutoSkipNotificationText"
];
const booleanConfigurationFields = [
    "AutoDetectIntros", "AnalyzeMovies", "AnalyzeSeasonZero", "SelectAllLibraries", "UpdateMediaSegments", "UseAlternativeBlackFrameAnalyzer", "UseChapterMarkersBlackFrame", "FullLengthChapters", "RebuildMediaSegments", "ScanIntroduction", "ScanCredits", "ScanRecap", "ScanPreview", "PreferChromaprint", "CacheFingerprints", "PluginSkip", "AutoSkip", "SkipFirstEpisode", "SnapToKeyframe", "AdjustIntroBasedOnSilence", "AdjustIntroBasedOnChapters"
];

// ========== UTILITY FUNCTIONS ========== //
const addItem = (select, text, value) => {
    let item = new Option(text, value);
    select.add(item);
};
const clearSelect = (select) => {
    timestampError.value = "";
    if (typeof timestampErrorDiv !== 'undefined') {
    if (typeof timestampEditor !== 'undefined') {
    if (typeof timeContainer !== 'undefined') {
    if (typeof canvas !== 'undefined') {
    for (let i = select.options.length - 1; i >= 0; i--) select.remove(i);
};

const getJson = async (url) => {
    try {
        const r = await fetchWithAuth(url, "GET");
        return r.ok ? r.json() : null;
    } catch (err) {
        console.debug(err);
        return null;
    }
};

const fetchWithAuth = async (url, method, body) => {
    url = ApiClient.serverAddress() + "/" + url;
    const reqInit = {
        method,
        headers: { Authorization: "MediaBrowser Token=" + ApiClient.accessToken() },
        body
    };
    if (method === "POST") reqInit.headers["Content-Type"] = "application/json";
    return fetch(url, reqInit);
};

// ========== EVENT HANDLERS & PAGE LOGIC ========== //
const autoSkipChanged = () => {
    if (autoSkip.checked) {
        autoSkipClientList.style.display = "none";
    } else {
        autoSkipClientList.style.display = "unset";
        autoSkipClientList.style.width = "100%";
    }
};
autoSkip.addEventListener("change", autoSkipChanged);

const selectAllLibrariesChanged = () => {
    librariesContainer.style.display = selectAllLibraries.checked ? "none" : "unset";
};
selectAllLibraries.addEventListener("change", selectAllLibrariesChanged);

const fullLengthChaptersChanged = () => {
    recapPreviewDurations.style.display = fullLengthChapters.checked ? "none" : "unset";
};
fullLengthChapters.addEventListener("change", fullLengthChaptersChanged);

// Initialization logic (runs on page show)
document.querySelector("#TemplateConfigPage").addEventListener("pageshow", () => {
    Dashboard.showLoadingMsg();
    ApiClient.getPluginConfiguration("c83d86bb-a1e0-4c35-a113-e2101cf4ee6b")
        .then((config) => {
            for (const field of configurationFields) {
                document.querySelector("#" + field).value = config[field];
            }
            for (const field of booleanConfigurationFields) {
                document.querySelector("#" + field).checked = config[field];
            }
            analyzeMoviesChanged();
            populateLibraries();
            selectAllLibrariesChanged();
            fullLengthChaptersChanged();
            autoSkipChanged();
            generateAutoSkipTypeList();
            generateAutoSkipClientList();
            pluginSkipSettingVisible();
            alternativeBlackFrameAnalyzerSettingsVisible();
            adjustSettingsVisible();
            globalTimestampChanged();
            Dashboard.hideLoadingMsg();
        })
        .catch(() => Dashboard.hideLoadingMsg());
});

// Form submission handler
FingerprintConfigForm.addEventListener("submit", (e) => {
    Dashboard.showLoadingMsg();
    ApiClient.getPluginConfiguration("c83d86bb-a1e0-4c35-a113-e2101cf4ee6b")
        .then((config) => {
            for (const field of configurationFields) {
                config[field] = document.querySelector("#" + field).value;
            }
            for (const field of booleanConfigurationFields) {
                config[field] = document.querySelector("#" + field).checked;
            }
            ApiClient.updatePluginConfiguration("c83d86bb-a1e0-4c35-a113-e2101cf4ee6b", config)
                .then((result) => {
                    Dashboard.hideLoadingMsg();
                    Dashboard.processPluginConfigurationUpdateResult(result);
                })
                .catch(() => Dashboard.hideLoadingMsg());
        })
        .catch(() => Dashboard.hideLoadingMsg());
    e.preventDefault();
    return false;
});

// Helper to update the list of checked items in a checkbox list
const updateList = (textField, container) => {
    textField.value = Array.from(container.querySelectorAll('input[type="checkbox"]:checked'))
        .map((checkbox) => checkbox.nextElementSibling.textContent)
        .join(", ");
};

// Helper to generate a checkbox list
const generateCheckboxList = (items, containerId, textFieldId) => {
    const container = document.getElementById(containerId);
    const checkedItems = new Set(document.getElementById(textFieldId).value.split(", ").filter(Boolean));
    const fragment = document.createDocumentFragment();
    for (const item of items) {
        const label = document.createElement("label");
        label.className = "emby-checkbox-label";
        label.innerHTML = '<input type="checkbox" is="emby-checkbox"' + (checkedItems.has(item) ? " checked" : "") + ">" + '<span class="checkboxLabel">' + item + "</span>";
        fragment.appendChild(label);
    }
    container.innerHTML = "";
    container.appendChild(fragment);
    container.addEventListener(
        "change",
        (e) => {
            if (e.target.type === "checkbox") updateList(document.getElementById(textFieldId), container);
        },
        { passive: true },
    );
};

// Generate client and type lists for auto skip
const generateAutoSkipClientList = async () => {
    const response = await getJson("Devices");
    const devices = [...new Set(response.Items.map((item) => item.AppName))];
    generateCheckboxList(devices, "autoSkipCheckboxes", "ClientList");
};
const generateAutoSkipTypeList = async () => {
    const types = ["Introduction", "Credits", "Recap", "Preview"];
    generateCheckboxList(types, "autoSkipTypeCheckboxes", "TypeList");
};

// Populate libraries for selection
const populateLibraries = async () => {
    const response = await getJson("Library/VirtualFolders");
    const tvLibraries = response.filter((item) => item.CollectionType === undefined || item.CollectionType === "tvshows" || item.CollectionType === "movies");
    const libraryNames = tvLibraries.map((lib) => lib.Name || "Unnamed Library");
    generateCheckboxList(libraryNames, "libraryCheckboxes", "SelectedLibraries");
};

// Show/hide server skip settings
const pluginSkipSettingVisible = async () => {
    if (pluginSkip.checked || autoSkip.checked) {
        pluginSkip.checked = true;
        serverSkipSettings.style.display = "unset";
    } else {
        serverSkipSettings.style.display = "none";
    }
};
pluginSkip.addEventListener("change", pluginSkipSettingVisible);

// Show/hide alternative black frame analyzer settings
const alternativeBlackFrameAnalyzerSettingsVisible = async () => {
    chapterMarkersBlackFrameSetting.style.display = useAlternativeBlackFrameAnalyzer.checked ? "none" : "unset";
};
useAlternativeBlackFrameAnalyzer.addEventListener("change", alternativeBlackFrameAnalyzerSettingsVisible);

// Show/hide silence settings
const adjustSettingsVisible = async () => {
    silenceSettings.style.display = adjustIntroBasedOnSilence.checked ? "unset" : "none";
};
adjustIntroBasedOnSilence.addEventListener("change", adjustSettingsVisible);

// Analyze movies toggle
const analyzeMoviesChanged = async () => {
    document.getElementById("movieCreditsDuration").style.display = analyzeMovies.checked ? "unset" : "none";
};
analyzeMovies.addEventListener("change", analyzeMoviesChanged);

// Global timestamp dropdown
const globalTimestampChanged = () => {
    btnEraseGlobalTimestamps.textContent = "Erase all " + globalTimestamps.value + " timestamps (globally)";
};
globalTimestamps.addEventListener("change", globalTimestampChanged);
globalTimestamps.addEventListener("click", () => {
    globalTimestamps.removeEventListener("change", globalTimestampChanged);
    globalTimestamps.addEventListener("change", globalTimestampChanged);
});

// ========== SUPPORT, STORAGE ========== //

support.addEventListener("toggle", async () => {
    if (!support.open) return;
    const bundle = await fetchWithAuth("IntroSkipper/SupportBundle", "GET", null);
    const bundleText = await bundle.text();
    const ta = document.querySelector("textarea#supportBundle");
    ta.value = bundleText;
    ta.focus();
    ta.setSelectionRange(0, ta.value.length);
    try {
        navigator.clipboard.writeText(bundleText);
        Dashboard.alert("Support bundle copied to clipboard");
    } catch {
        Dashboard.alert("Press Ctrl+C to copy support bundle");
    }
});

storage.addEventListener("toggle", async () => {
    if (!storage.open) return;
    const bundle = await fetchWithAuth("IntroSkipper/Storage", "GET", null);
    const bundleText = await bundle.text();
    document.querySelector("textarea#storageText").value = bundleText;
});

// ========== ADVANCED: ERASE, REBUILD, ETC. ========== //

btnEraseGlobalTimestamps.addEventListener("click", (e) => {
    const eraseTimestamps = (mode) => {
        const eraseCacheChecked = document.getElementById("eraseModeCacheCheckbox").checked;
        Dashboard.confirm(
            `Are you sure you want to erase all previously discovered ${mode.toLowerCase()} timestamps?`,
            "Confirm timestamp erasure",
            (result) => {
                if (!result) return;
                fetchWithAuth(`Intros/EraseTimestamps?mode=${mode}&eraseCache=${eraseCacheChecked}`, "POST", null);
                Dashboard.alert(`${mode} timestamps erased`);
                document.getElementById("eraseModeCacheCheckbox").checked = false;
            }
        );
    };
    switch (globalTimestamps.value) {
        case "introduction": eraseTimestamps("Introduction"); break;
        case "recap": eraseTimestamps("Recap"); break;
        case "credits": eraseTimestamps("Credits"); break;
        case "preview": eraseTimestamps("Preview"); break;
        default: return;
    }
    e.preventDefault();
});

btnRebuildDatabase.addEventListener("click", () => {
    fetchWithAuth("Intros/RebuildDatabase", "POST", null);
});

// ========== VISUALIZER LOGIC ========== //

const visualizerToggled = async () => {
    if (!visualizer.open) {
        analyzerActionsSection.style.display = "none";
        saveAnalyzerActionsButton.style.display = "none";
        scanSeasonButton.style.display = "none";
        return;
    }
    selectShow.innerHTML = "";
    Dashboard.showLoadingMsg();
    shows = await getJson("Intros/Shows");
    let showsByLibrary = {};
    for (const show in shows) {
        const libraryName = shows[show].LibraryName || "Uncategorized";
        if (!showsByLibrary[libraryName]) showsByLibrary[libraryName] = [];
        showsByLibrary[libraryName].push({
            value: show,
            text: shows[show].SeriesName + " (" + shows[show].ProductionYear + ")",
        });
    }
    for (const library in showsByLibrary) {
        const optgroup = document.createElement("optgroup");
        optgroup.label = library;
        showsByLibrary[library].forEach((show) => {
            const option = document.createElement("option");
            option.value = show.value;
            option.textContent = show.text;
            optgroup.appendChild(option);
        });
        selectShow.appendChild(optgroup);
    }
    selectShow.value = "";
    Dashboard.hideLoadingMsg();
};

const showChanged = async () => {
    seasonSelection.style.display = "unset";
    clearSelect(selectSeason);
    eraseSeasonContainer.style.display = "none";
    eraseMovieContainer.style.display = "none";
    episodeSelection.style.display = "unset";
    clearSelect(selectEpisode1);
    clearSelect(selectEpisode2);
    if (shows[selectShow.value].IsMovie) {
        await movieLoaded();
        return;
    }
    for (const season in shows[selectShow.value].Seasons) {
        addItem(selectSeason, "Season " + shows[selectShow.value].Seasons[season], season);
    }
    selectSeason.value = "";
};

const seasonChanged = async () => {
    const seasonData = encodeURI(selectShow.value) + "/" + encodeURI(selectSeason.value);
    Dashboard.showLoadingMsg();
    saveAnalyzerActionsButton.style.display = "block";
    saveAnalyzerActionsButton.textContent = "Apply to season";
    scanSeasonButton.style.display = "block";
    scanSeasonButton.textContent = "Scan season";
    const analyzerActions = await getJson("Intros/AnalyzerActions/" + encodeURI(selectSeason.value));
    actionIntro.value = analyzerActions.Introduction || "Default";
    actionCredits.value = analyzerActions.Credits || "Default";
    actionRecap.value = analyzerActions.Recap || "Default";
    actionPreview.value = analyzerActions.Preview || "Default";
    analyzerActionsSection.style.display = "unset";
    eraseSeasonContainer.style.display = "unset";
    clearSelect(selectEpisode1);
    clearSelect(selectEpisode2);
    let i = 1;
    const episodes = await getJson("Intros/Show/" + seasonData);
    for (const episode in episodes) {
        const strI = i.toLocaleString("en", { minimumIntegerDigits: 2, maximumFractionDigits: 0 });
        addItem(selectEpisode1, strI + ": " + episodes[episode].Name, episodes[episode].Id);
        addItem(selectEpisode2, strI + ": " + episodes[episode].Name, episodes[episode].Id);
        i++;
    }
    Dashboard.hideLoadingMsg();
    setTimeout(() => {
        selectEpisode1.selectedIndex = 0;
        selectEpisode2.selectedIndex = 1;
        episodeChanged();
    }, 100);
};

const episodeChanged = async () => {
    if (!selectEpisode1.value || !selectEpisode2.value) return;
    Dashboard.showLoadingMsg();
    timestampError.value = "";
    fingerprintVisualizer.style.display = "unset";
    canvas.style.display = "none";
    lhs = await getJson("Intros/Episode/" + selectEpisode1.value + "/Chromaprint");
    if (lhs === undefined) {
        timestampError.value += "Error: " + selectEpisode1.value + " fingerprints failed!\n";
    } else if (lhs === null) {
        timestampError.value += selectEpisode1.value + " fingerprints missing or incomplete.\n";
    }
    rightEpisodeEditor.style.display = "unset";
    rhs = await getJson("Intros/Episode/" + selectEpisode2.value + "/Chromaprint");
    if (rhs === undefined) {
        timestampError.value += "Error: " + selectEpisode2.value + " fingerprints failed!";
    } else if (rhs === null) {
        timestampError.value += selectEpisode2.value + " fingerprints missing or incomplete.\n";
    }
    if (timestampError.value === "") {
        timestampErrorDiv.style.display = "none";
    } else {
        timestampErrorDiv.style.display = "unset";
    }
    Dashboard.hideLoadingMsg();
    txtOffset.value = "0";
    refreshBounds();
    renderTroubleshooter();
    findExactMatches();
    await updateTimestampEditor();
};

const movieLoaded = async () => {
    Dashboard.showLoadingMsg();
    saveAnalyzerActionsButton.textContent = "Apply to movie";
    scanSeasonButton.textContent = "Scan movie";
    seasonSelection.style.display = "none";
    episodeSelection.style.display = "none";
    eraseMovieContainer.style.display = "unset";
    scanSeasonButton.style.display = "block";
    timestampError.value = "";
    fingerprintVisualizer.style.display = "none";
    rightEpisodeEditor.style.display = "none";
    if (timestampError.value === "") {
        timestampErrorDiv.style.display = "none";
    } else {
        timestampErrorDiv.style.display = "unset";
    }
    Dashboard.hideLoadingMsg();
    txtOffset.value = "0";
    await updateTimestampEditor();
};

const setupTimeInputs = () => {
    timestampEditor.querySelectorAll(".inputContainer").forEach((container) => {
        const displayInput = container.querySelector('[id$="Display"]');
        const editInput = container.querySelector('[id$="Edit"]');
        displayInput.addEventListener("pointerdown", (e) => {
            e.preventDefault();
            switchToEdit(displayInput, editInput);
        });
        editInput.addEventListener("blur", () => switchToDisplay(displayInput, editInput));
        displayInput.value = formatTime(parseFloat(editInput.value) || 0);
    });
};

const switchToEdit = (displayInput, editInput) => {
    displayInput.style.display = "none";
    editInput.style.display = "";
    editInput.focus();
};
const switchToDisplay = (displayInput, editInput) => {
    editInput.style.display = "none";
    displayInput.style.display = "";
    displayInput.value = formatTime(parseFloat(editInput.value) || 0);
};
const formatTime = (totalSeconds) => {
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = Math.floor(totalSeconds % 60);
    let result = [];
    if (hours > 0) result.push(hours + " hour" + (hours !== 1 ? "s" : ""));
    if (minutes > 0) result.push(minutes + " minute" + (minutes !== 1 ? "s" : ""));
    if (seconds > 0 || result.length === 0) result.push(seconds + " second" + (seconds !== 1 ? "s" : ""));
    return result.join(" ");
};

// updates the timestamp editor
async function updateTimestampEditor() {
    // Get the title and ID of the left and right episodes
    const leftEpisode = selectEpisode1.options[selectEpisode1.selectedIndex] || selectShow;

    // Try to get the timestamps of each intro, falling back a default value of zero if no intro was found
    const leftEpisodeJson = await getJson("Episode/" + leftEpisode.value + "/Timestamps");

    // Update the editor for the first episode
    timestampEditor.style.display = "unset";
    document.querySelector("#editLeftEpisodeTitle").textContent = leftEpisode.text;
    document.querySelector("#editLeftIntroEpisodeStartEdit").value = leftEpisodeJson.Introduction.Start;
    document.querySelector("#editLeftIntroEpisodeEndEdit").value = leftEpisodeJson.Introduction.End;
    document.querySelector("#editLeftCreditEpisodeStartEdit").value = leftEpisodeJson.Credits.Start;
    document.querySelector("#editLeftCreditEpisodeEndEdit").value = leftEpisodeJson.Credits.End;
    document.querySelector("#editLeftRecapEpisodeStartEdit").value = leftEpisodeJson.Recap.Start;
    document.querySelector("#editLeftRecapEpisodeEndEdit").value = leftEpisodeJson.Recap.End;
    document.querySelector("#editLeftPreviewEpisodeStartEdit").value = leftEpisodeJson.Preview.Start;
    document.querySelector("#editLeftPreviewEpisodeEndEdit").value = leftEpisodeJson.Preview.End;

    // Update the editor for the second episode
    if (rightEpisodeEditor.style.display !== "none") {
        const rightEpisode = selectEpisode2.options[selectEpisode2.selectedIndex];

        // Try to get the timestamps of each intro, falling back a default value of zero if no intro was found
        const rightEpisodeJson = await getJson("Episode/" + rightEpisode.value + "/Timestamps");

        // Update the editor for the second episode
        document.querySelector("#editRightEpisodeTitle").textContent = rightEpisode.text;
        document.querySelector("#editRightIntroEpisodeStartEdit").value = rightEpisodeJson.Introduction.Start;
        document.querySelector("#editRightIntroEpisodeEndEdit").value = rightEpisodeJson.Introduction.End;
        document.querySelector("#editRightCreditEpisodeStartEdit").value = rightEpisodeJson.Credits.Start;
        document.querySelector("#editRightCreditEpisodeEndEdit").value = rightEpisodeJson.Credits.End;
        document.querySelector("#editRightRecapEpisodeStartEdit").value = rightEpisodeJson.Recap.Start;
        document.querySelector("#editRightRecapEpisodeEndEdit").value = rightEpisodeJson.Recap.End;
        document.querySelector("#editRightPreviewEpisodeStartEdit").value = rightEpisodeJson.Preview.Start;
        document.querySelector("#editRightPreviewEpisodeEndEdit").value = rightEpisodeJson.Preview.End;
    }

    // Update display inputs
    const inputs = document.querySelectorAll('#timestampEditor input[type="number"]');
    inputs.forEach((input) => {
        const displayInput = document.getElementById(input.id.replace("Edit", "Display"));
        displayInput.value = formatTime(parseFloat(input.value) || 0);
    });

    setupTimeInputs();
}

const keyDown = (e) => {
    let episodeDelta = 0;
    let offsetDelta = 0;
    switch (e.key) {
        case "ArrowDown":
            if (timestampError.value !== "") offsetDelta = e.ctrlKey ? 10 / 0.1238 : 1;
            break;
        case "ArrowUp":
            if (timestampError.value !== "") {
            break;
        case "ArrowRight": episodeDelta = 2; break;
        case "ArrowLeft": episodeDelta = -2; break;
        default: return;
    }
    if (offsetDelta !== 0) {
    if (episodeDelta !== 0) {
        const lhsRemaining = selectEpisode1.selectedIndex;
        const rhsRemaining = selectEpisode2.length - selectEpisode2.selectedIndex - 1;
        if (episodeDelta > 0 && rhsRemaining <= 1) return;
        else if (episodeDelta < 0 && lhsRemaining <= 1) {
        selectEpisode1.selectedIndex += episodeDelta;
        selectEpisode2.selectedIndex += episodeDelta;
        episodeChanged();
    }
    renderTroubleshooter();
    e.preventDefault();
};

const checkWindowHash = () => {
    const h = location.hash;
    if (h === "#!/configurationpage?name=Intro%20Skipper" || h.includes("#!/dialog")) {
    document.removeEventListener("keydown", keyDown);
    clearInterval(windowHashInterval);
};

// Register all visualizer event listeners
visualizer.addEventListener("toggle", visualizerToggled);
selectShow.addEventListener("change", showChanged);
selectSeason.addEventListener("change", seasonChanged);
selectEpisode1.addEventListener("change", episodeChanged);
selectEpisode2.addEventListener("change", episodeChanged);
txtOffset.addEventListener("change", renderTroubleshooter);
document.addEventListener("keydown", keyDown);
windowHashInterval = setInterval(checkWindowHash, 2500);
