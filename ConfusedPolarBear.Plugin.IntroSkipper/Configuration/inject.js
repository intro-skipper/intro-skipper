let introSkipper = {
    allowEnter: true,
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
      
      // Check for context root and set id accordingly
      let path_arr = path.split("/");
      let id = "";
      if (path_arr[1] == "Items") {
        id = path_arr[2];
      } else {
        id = path_arr[3];
      }

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
    if (location !== "#/video") {
      introSkipper.d("Ignoring location change");
      return;
    }
    introSkipper.injectCss();
    introSkipper.injectButton();
    document.body.addEventListener('keydown', introSkipper.eventHandler, true);
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
    :root {
        --rounding: .2em;
        --accent: 0, 164, 220;
    }
    #skipIntro.upNextContainer {
        width: unset;
    }
    #skipIntro {
        position: absolute;
        bottom: 6em;
        right: 4.5em;
        background-color: transparent;
        font-size: 1.2em;
    }
    #skipIntro .emby-button {
        text-shadow: 0 0 3px rgba(0, 0, 0, 0.7);
        border-radius: var(--rounding);
        background-color: rgba(0, 0, 0, 0.3);
        will-change: opacity, transform;
        opacity: 0;
        transition: opacity 0.3s ease-in, transform 0.3s ease-out;
    }
    #skipIntro .emby-button:hover,
    #skipIntro .emby-button:focus {
        background-color: rgba(var(--accent),0.7);
        transform: scale(1.05);
    }
    #btnSkipSegmentText {
        padding-right: 0.15em;
        padding-left: 0.2em;
        margin-top: -0.1em;
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
    <button is="emby-button" type="button" class="btnSkipIntro injected">
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
    if (!skipButton || !introSkipper.allowEnter) {
        return;
    }
    const embyButton = skipButton.querySelector(".emby-button");
    const segment = introSkipper.getCurrentSegment(introSkipper.videoPlayer.currentTime);
    switch (segment.SegmentType) {
        case "None":
            if (embyButton.style.opacity === '0') return;

            embyButton.style.opacity = '0';
            embyButton.addEventListener("transitionend", () => {
                skipButton.classList.add("hide");
            }, { once: true });
            return;
        case "Introduction":
            skipButton.querySelector("#btnSkipSegmentText").textContent = skipButton.dataset.intro_text;
            break;
        case "Credits":
            skipButton.querySelector("#btnSkipSegmentText").textContent = skipButton.dataset.credits_text;
            break;
    }
    if (!skipButton.classList.contains("hide")) return;

    skipButton.classList.remove("hide");
    embyButton.offsetWidth; // Force reflow
    requestAnimationFrame(() => {
        embyButton.style.opacity = '1';
    });
}
/** Debounce function to limit the rate at which a function can fire. */
function debounce(func, wait) {
    let timeout;
    return function(...args) {
        const context = this;
        clearTimeout(timeout);
        timeout = setTimeout(() => func.apply(context, args), wait);
    };
}
/** Seeks to the end of the intro. */
introSkipper.doSkip = debounce(function (e) {
    introSkipper.d("Skipping intro");
    introSkipper.d(introSkipper.skipSegments);
    const segment = introSkipper.getCurrentSegment(introSkipper.videoPlayer.currentTime);
    if (segment.SegmentType === "None") {
        console.warn("[intro skipper] doSkip() called without an active segment");
        return;
    }
    // Disable keydown events
    introSkipper.allowEnter = false;
    introSkipper.videoPlayer.currentTime = segment.IntroEnd;
    // Listen for the seeked event to re-enable keydown events
    const onSeeked = () => {
        introSkipper.allowEnter = true;
        introSkipper.videoPlayer.removeEventListener('seeked', onSeeked);
    };
    introSkipper.videoPlayer.addEventListener('seeked', onSeeked);
}, 1000);
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
/** Handle keydown events. */
introSkipper.eventHandler = function (e) {
    const skipButton = document.querySelector("#skipIntro");
    if (!skipButton || skipButton.classList.contains("hide")) {
        return;
    }
    // Ignore all keydown events
    if (!introSkipper.allowEnter) {
        e.preventDefault();
        return;
    }
    if (e.key === "Enter") {
        e.preventDefault();
        e.stopPropagation();
        introSkipper.doSkip();
    }
}
introSkipper.setup();
