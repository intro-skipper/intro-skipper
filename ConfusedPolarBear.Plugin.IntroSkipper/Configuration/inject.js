let introSkipper = {
    skipSegments: {},
    videoPlayer: {},
    // .bind() is used here to prevent illegal invocation errors
    originalFetch: window.fetch.bind(window),
};
introSkipper.d = function (msg) {
    console.debug("[intro skipper] ", msg);
  }
  /** Setup event listeners */
  introSkipper.setup = function () {
    document.addEventListener("viewshow", introSkipper.viewShow);
    window.fetch = introSkipper.fetchWrapper;
    introSkipper.d("Registered hooks");
  }
  /** Wrapper around fetch() that retrieves skip segments for the currently playing item. */
  introSkipper.fetchWrapper = async function (...args) {
    // Based on JellyScrub's trickplay.js
    let [resource, options] = args;
    let response = await introSkipper.originalFetch(resource, options);
    // Bail early if this isn't a playback info URL
    try {
      let path = new URL(resource).pathname;
      if (!path.includes("/PlaybackInfo")) { return response; }
      introSkipper.d("Retrieving skip segments from URL");
      introSkipper.d(path);
      let id = path.split("/")[2];
      introSkipper.skipSegments = await introSkipper.secureFetch(`Episode/${id}/IntroSkipperSegments`);
      introSkipper.d("Successfully retrieved skip segments");
      introSkipper.d(introSkipper.skipSegments);
    }
    catch (e) {
      console.error("Unable to get skip segments from", resource, e);
    }
    return response;
  }
  /**
  * Event handler that runs whenever the current view changes.
  * Used to detect the start of video playback.
  */
  introSkipper.viewShow = function () {
    const location = window.location.hash;
    introSkipper.d("Location changed to " + location);
    if (location !== "#!/video") {
      introSkipper.d("Ignoring location change");
      return;
    }
    introSkipper.injectCss();
    introSkipper.injectButton();
    introSkipper.videoPlayer = document.querySelector("video");
    if (introSkipper.videoPlayer != null) {
      introSkipper.d("Hooking video timeupdate");
      introSkipper.videoPlayer.addEventListener("timeupdate", introSkipper.videoPositionChanged);
    }
  }
  /**
  * Injects the CSS used by the skip intro button.
  * Calling this function is a no-op if the CSS has already been injected.
  */
  introSkipper.injectCss = function () {
    if (introSkipper.testElement("style#introSkipperCss")) {
      introSkipper.d("CSS already added");
      return;
    }
    introSkipper.d("Adding CSS");
    let styleElement = document.createElement("style");
    styleElement.id = "introSkipperCss";
    styleElement.innerText = `
    @media (hover:hover) and (pointer:fine) {
        #skipIntro .paper-icon-button-light:hover:not(:disabled) {
            color: black !important;
            background-color: rgba(47, 93, 98, 0) !important;
        }
    }
    #skipIntro .paper-icon-button-light.show-focus:focus {
        transform: scale(1.04) !important;
    }
    #skipIntro.upNextContainer {
        width: unset;
    }
    #skipIntro {
        padding: 0 1px;
        position: absolute;
        right: 10em;
        bottom: 9em;
        background-color: rgba(25, 25, 25, 0.66);
        border: 1px solid;
        border-radius: 0px;
        display: inline-block;
        cursor: pointer;
        box-shadow: inset 0 0 0 0 #f9f9f9;
        -webkit-transition: ease-out 0.4s;
        -moz-transition: ease-out 0.4s;
        transition: ease-out 0.4s;
    }
    #skipIntro #btnSkipSegmentText {
        padding-right: 3px;
        padding-bottom: 2px;
    }
    @media (max-width: 1080px) {
        #skipIntro {
            right: 10%;
        }
    }
    #skipIntro:hover {
        box-shadow: inset 400px 0 0 0 #f9f9f9;
        -webkit-transition: ease-in 1s;
        -moz-transition: ease-in 1s;
        transition: ease-in 1s;
    }
    `;
    document.querySelector("head").appendChild(styleElement);
}
/**
 * Inject the skip intro button into the video player.
 * Calling this function is a no-op if the CSS has already been injected.
 */
introSkipper.injectButton = async function () {
    // Ensure the button we're about to inject into the page doesn't conflict with a pre-existing one
    const preExistingButton = introSkipper.testElement("div.skipIntro");
    if (preExistingButton) {
        preExistingButton.style.display = "none";
    }
    if (introSkipper.testElement(".btnSkipIntro.injected")) {
        introSkipper.d("Button already added");
        return;
    }
    introSkipper.d("Adding button");
    let config = await introSkipper.secureFetch("Intros/UserInterfaceConfiguration");
    if (!config.SkipButtonVisible) {
        introSkipper.d("Not adding button: not visible");
        return;
    }
    // Construct the skip button div
    const button = document.createElement("div");
    button.id = "skipIntro"
    button.classList.add("hide");
    button.addEventListener("click", introSkipper.doSkip);
    button.innerHTML = `
    <button is="paper-icon-button-light" class="btnSkipIntro paper-icon-button-light injected">
        <span id="btnSkipSegmentText"></span>
        <span class="material-icons skip_next"></span>
    </button>
    `;
    button.dataset["intro_text"] = config.SkipButtonIntroText;
    button.dataset["credits_text"] = config.SkipButtonEndCreditsText;
    /*
    * Alternative workaround for #44. Jellyfin's video component registers a global click handler
    * (located at src/controllers/playback/video/index.js:1492) that pauses video playback unless
    * the clicked element has a parent with the class "videoOsdBottom" or "upNextContainer".
    */
    button.classList.add("upNextContainer");
    // Append the button to the video OSD
    let controls = document.querySelector("div#videoOsdPage");
    controls.appendChild(button);
}
/** Tests if the OSD controls are visible. */
introSkipper.osdVisible = function () {
    const osd = document.querySelector("div.videoOsdBottom");
    return osd ? !osd.classList.contains("hide") : false;
}
/** Get the currently playing skippable segment. */
introSkipper.getCurrentSegment = function (position) {
    for (let key in introSkipper.skipSegments) {
        const segment = introSkipper.skipSegments[key];
        if ((position >= segment.ShowSkipPromptAt && position < segment.HideSkipPromptAt) || (introSkipper.osdVisible() && position >= segment.IntroStart && position < segment.IntroEnd)) {
            segment["SegmentType"] = key;
            return segment;
        }
    }
    return { "SegmentType": "None" };
}
/** Playback position changed, check if the skip button needs to be displayed. */
introSkipper.videoPositionChanged = function () {
    const skipButton = document.querySelector("#skipIntro");
    if (!skipButton) {
        return;
    }
    const segment = introSkipper.getCurrentSegment(introSkipper.videoPlayer.currentTime);
    switch (segment["SegmentType"]) {
        case "None":
            skipButton.classList.add("hide");
            return;
        case "Introduction":
            skipButton.querySelector("#btnSkipSegmentText").textContent =
                skipButton.dataset["intro_text"];
            break;
        case "Credits":
            skipButton.querySelector("#btnSkipSegmentText").textContent =
                skipButton.dataset["credits_text"];
            break;
    }
    skipButton.classList.remove("hide");
}
/** Seeks to the end of the intro. */
introSkipper.doSkip = function (e) {
    introSkipper.d("Skipping intro");
    introSkipper.d(introSkipper.skipSegments);
    const segment = introSkipper.getCurrentSegment(introSkipper.videoPlayer.currentTime);
    if (segment["SegmentType"] === "None") {
        console.warn("[intro skipper] doSkip() called without an active segment");
        return;
    }
    introSkipper.videoPlayer.currentTime = segment["IntroEnd"];
}
/** Tests if an element with the provided selector exists. */
introSkipper.testElement = function (selector) { return document.querySelector(selector); }
/** Make an authenticated fetch to the Jellyfin server and parse the response body as JSON. */
introSkipper.secureFetch = async function (url) {
    url = ApiClient.serverAddress() + "/" + url;
    const reqInit = { headers: { "Authorization": "MediaBrowser Token=" + ApiClient.accessToken() } };
    const res = await fetch(url, reqInit);
    if (res.status !== 200) { throw new Error(`Expected status 200 from ${url}, but got ${res.status}`); }
    return await res.json();
}
introSkipper.setup();
