const introSkipper = {
    originalFetch: window.fetch.bind(window),
    d: msg => console.debug("[intro skipper] ", msg),
    setup() {
        this.initializeState();
        document.addEventListener("viewshow", this.viewShow.bind(this));
        window.fetch = this.fetchWrapper.bind(this);
        this.videoPositionChanged = this.videoPositionChanged.bind(this);
        this.d("Registered hooks");
    },
    initializeState() {
        Object.assign(this, { allowEnter: true, skipSegments: {}, videoPlayer: null, skipButton: null, osdElement: null, skipperData: null, currentEpisodeId: null, injectMetadata: false });
    },
    /** Wrapper around fetch() that retrieves skip segments for the currently playing item. */
    async fetchWrapper(resource, options) {
        const response = await this.originalFetch(resource, options);
        try {
            const pathname = new URL(resource).pathname;
            if (pathname.includes("/PlaybackInfo")) {
                this.d(`Retrieving skip segments from URL ${pathname}`);
                const pathArr = pathname.split("/");
                const id = pathArr[pathArr.indexOf("Items") + 1] || pathArr[3];
                this.skipSegments = await this.secureFetch(`Episode/${id}/IntroSkipperSegments`);
                this.d("Retrieved skip segments", this.skipSegments);
            } else if (this.injectMetadata && pathname.includes("/MetadataEditor")) {
                this.d(`Metadata editor detected, URL ${pathname}`);
                const pathArr = pathname.split("/");
                this.currentEpisodeId = pathArr[pathArr.indexOf("Items") + 1] || pathArr[3];
                this.skipperData = await this.secureFetch(`Episode/${this.currentEpisodeId}/Timestamps`);
                if (this.skipperData) {
                    const metadataFormFields = document.querySelector('.metadataFormFields');
                    metadataFormFields && requestAnimationFrame(() => this.injectSkipperFields(metadataFormFields));
                }
            }
        } catch (e) {
            console.error("Error processing", resource, e);
        }
        return response;
    },
    /**
     * Event handler that runs whenever the current view changes.
     * Used to detect the start of video playback.
     */
    viewShow() {
        const location = window.location.hash;
        this.d(`Location changed to ${location}`);
        this.allowEnter = true;
        this.injectMetadata = /#\/(tv|details|home|search)/.test(location);
        if (location === "#/video") {
            this.injectCss();
            this.injectButton();
            this.videoPlayer = document.querySelector("video");
            if (this.videoPlayer) {
                this.d("Hooking video timeupdate");
                this.videoPlayer.addEventListener("timeupdate", this.videoPositionChanged);
                this.osdElement = document.querySelector("div.videoOsdBottom")
            }
        }   
    },
    /**
     * Injects the CSS used by the skip intro button.
     * Calling this function is a no-op if the CSS has already been injected.
     */
    injectCss() {
        if (document.querySelector("style#introSkipperCss")) {
            this.d("CSS already added");
            return;
        }
        this.d("Adding CSS");
        const styleElement = document.createElement("style");
        styleElement.id = "introSkipperCss";
        styleElement.textContent = `
            :root {
                --rounding: 4px;
                --accent: 0, 164, 220;
            }
            #skipIntro.upNextContainer {
                width: unset;
                margin: unset;
            }
            #skipIntro {
                position: absolute;
                bottom: 7.5em;
                right: 5em;
                background-color: transparent;
            }
            #skipIntro .emby-button {
                color: #ffffff;
                font-size: 110%;
                background: rgba(0, 0, 0, 0.7);
                border-radius: var(--rounding);
                box-shadow: 0 0 4px rgba(0, 0, 0, 0.6);
                transition: opacity 0.3s cubic-bezier(0.4,0,0.2,1),
                            transform 0.3s cubic-bezier(0.4,0,0.2,1),
                            background-color 0.2s ease-out,
                            box-shadow 0.2s ease-out;
                opacity: 0;
                transform: translateY(50%);
            }
            #skipIntro.show .emby-button {
                opacity: 1;
                transform: translateY(0);
            }
            #skipIntro .emby-button:hover {
                background: rgb(var(--accent));
                box-shadow: 0 0 8px rgba(var(--accent), 0.6);
                filter: brightness(1.2);
            }
            #skipIntro .emby-button:focus:not(:focus-visible) {
                background: rgb(var(--accent));
                box-shadow: 0 0 8px rgba(var(--accent), 0.6);
            }
            #btnSkipSegmentText {
                letter-spacing: 0.5px;
                padding: 0 5px 0 5px;
            }
        `;
        document.querySelector("head").appendChild(styleElement);
    },
    /**
     * Inject the skip intro button into the video player.
     * Calling this function is a no-op if the CSS has already been injected.
     */
    async injectButton() {
        // Ensure the button we're about to inject into the page doesn't conflict with a pre-existing one
        const preExistingButton = document.querySelector("div.skipIntro");
        if (preExistingButton) {
            preExistingButton.style.display = "none";
        }
        if (document.querySelector(".btnSkipIntro.injected")) {
            this.d("Button already added");
            this.skipButton = document.querySelector("#skipIntro");
            return;
        }
        const config = await this.secureFetch("Intros/UserInterfaceConfiguration");
        if (!config.SkipButtonVisible) {
            this.d("Not adding button: not visible");
            return;
        }
        this.d("Adding button");
        this.skipButton = document.createElement("div");
        this.skipButton.id = "skipIntro";
        this.skipButton.classList.add("hide", "upNextContainer");
        this.skipButton.addEventListener("click", this.doSkip.bind(this));
        this.skipButton.addEventListener("keydown", this.eventHandler.bind(this));
        this.skipButton.innerHTML = `
            <button is="emby-button" type="button" class="btnSkipIntro injected">
                <span id="btnSkipSegmentText"></span>
                <span class="material-icons">skip_next</span>
            </button>
        `;
        this.skipButton.dataset.Introduction = config.SkipButtonIntroText;
        this.skipButton.dataset.Credits = config.SkipButtonEndCreditsText;
        const controls = document.querySelector("div#videoOsdPage");
        controls.appendChild(this.skipButton);
    },
    /** Tests if the OSD controls are visible. */
    osdVisible() {
        return this.osdElement ? !this.osdElement.classList.contains("hide") : false;
    },
    /** Get the currently playing skippable segment. */
    getCurrentSegment(position) {
        for (const [key, segment] of Object.entries(this.skipSegments)) {
            if ((position > segment.ShowSkipPromptAt && position < segment.HideSkipPromptAt - 1) || 
                (this.osdVisible() && position > segment.IntroStart && position < segment.IntroEnd - 1)) {
                segment.SegmentType = key;
                return segment;
            }
        }
        return { SegmentType: "None" };
    },
    overrideBlur(button) {
        if (!button.originalBlur) {
            button.originalBlur = button.blur;
            button.blur = () => {
                if (!button.contains(document.activeElement)) {
                    button.originalBlur();
                }
            };
        }
    },
    restoreBlur(button) {
        if (button.originalBlur) {
            button.blur = button.originalBlur;
            delete button.originalBlur;
        }
    },
    /** Playback position changed, check if the skip button needs to be displayed. */
    videoPositionChanged() {
        if (!this.skipButton) return;
        const embyButton = this.skipButton.querySelector(".emby-button");
        const segmentType = introSkipper.getCurrentSegment(this.videoPlayer.currentTime).SegmentType;
        if (segmentType === "None") {
            if (!this.skipButton.classList.contains('show')) return;
            this.skipButton.classList.remove('show');
            embyButton.addEventListener("transitionend", () => {
                this.skipButton.classList.add("hide");
                this.restoreBlur(embyButton);
                embyButton.blur();
                this.allowEnter = true;
            }, { once: true });
            return;
        }
        this.skipButton.querySelector("#btnSkipSegmentText").textContent = this.skipButton.dataset[segmentType];
        if (!this.skipButton.classList.contains("hide")) {
            if (!this.osdVisible() && !embyButton.contains(document.activeElement)) embyButton.focus();
            return;
        }
        requestAnimationFrame(() => {
            this.skipButton.classList.remove("hide");
            requestAnimationFrame(() => {
                this.skipButton.classList.add('show');
                this.overrideBlur(embyButton);
                embyButton.focus();
            });
        });
    },
    /** Seeks to the end of the intro. */
    doSkip() {
        if (!this.allowEnter) return;
        const segment = this.getCurrentSegment(this.videoPlayer.currentTime);
        if (segment.SegmentType === "None") {
            console.warn("[intro skipper] doSkip() called without an active segment");
            return;
        }
        this.d(`Skipping ${segment.SegmentType}`);
        this.allowEnter = false;
        this.videoPlayer.currentTime = segment.SegmentType === "Credits" && this.videoPlayer.duration - segment.IntroEnd < 3
            ? this.videoPlayer.duration + 10
            : segment.IntroEnd;
    },
    injectSkipperFields(metadataFormFields) {
        const skipperFields = document.createElement('div');
        skipperFields.className = 'detailSection introskipperSection';
        skipperFields.innerHTML = `
            <h2>Intro Skipper</h2>
            <div class="inlineForm">
                <div class="inputContainer">
                    <label class="inputLabel inputLabelUnfocused" for="introStart">Intro Start</label>
                    <input type="number" id="introStart" step="any" min="0" class="emby-input" value="0">
                </div>
                <div class="inputContainer">
                    <label class="inputLabel inputLabelUnfocused" for="introEnd">Intro End</label>
                    <input type="number" id="introEnd" step="any" min="0" class="emby-input" value="0">
                </div>
            </div>
            <div class="inlineForm">
                <div class="inputContainer">
                    <label class="inputLabel inputLabelUnfocused" for="creditsStart">Credits Start</label>
                    <input type="number" id="creditsStart" step="any" min="0" class="emby-input" value="0">
                </div>
                <div class="inputContainer">
                    <label class="inputLabel inputLabelUnfocused" for="creditsEnd">Credits End</label>
                    <input type="number" id="creditsEnd" step="any" min="0" class="emby-input" value="0">
                </div>
            </div>
        `;
        metadataFormFields.appendChild(skipperFields);
        this.attachSaveListener();
        this.updateSkipperFields();
    },
    updateSkipperFields() {
        const { Introduction = {}, Credits = {} } = this.skipperData;
        document.getElementById('introStart').value = Introduction.IntroStart ?? 0;
        document.getElementById('introEnd').value = Introduction.IntroEnd ?? 0;
        document.getElementById('creditsStart').value = Credits.IntroStart ?? 0;
        document.getElementById('creditsEnd').value = Credits.IntroEnd ?? 0;
    },
    attachSaveListener() {
        const saveButton = document.querySelector('.formDialogFooter .btnSave');
        if (saveButton) {
            saveButton.addEventListener('click', this.saveSkipperData);
        } else {
            console.error('Save button not found');
        }
    },
    async saveSkipperData() {
        const newTimestamps = {
            Introduction: {
                IntroStart: parseFloat(document.getElementById('introStart').value || 0),
                IntroEnd: parseFloat(document.getElementById('introEnd').value || 0)
            },
            Credits: {
                IntroStart: parseFloat(document.getElementById('creditsStart').value || 0),
                IntroEnd: parseFloat(document.getElementById('creditsEnd').value || 0)
            }
        };
        const { Introduction = {}, Credits = {} } = this.skipperData;
        if (newTimestamps.Introduction.IntroStart !== (Introduction.IntroStart ?? 0) ||
            newTimestamps.Introduction.IntroEnd !== (Introduction.IntroEnd ?? 0) ||
            newTimestamps.Credits.IntroStart !== (Credits.IntroStart ?? 0) ||
            newTimestamps.Credits.IntroEnd !== (Credits.IntroEnd ?? 0)) {
            const response = await secureFetch(`Episode/${this.currentEpisodeId}/Timestamps`, "POST", JSON.stringify(newTimestamps));
            this.d(response.ok ? 'Timestamps updated successfully' : 'Failed to update timestamps:', response.status);
        } else {
            this.d('Timestamps have not changed, skipping update');
        }
    },
    /** Make an authenticated fetch to the Jellyfin server and parse the response body as JSON. */
    async secureFetch(url, method = "GET", body = null) {
        const response = await fetch(`${ApiClient.serverAddress()}/${url}`, {
            method,
            headers: { "Authorization": `MediaBrowser Token=${ApiClient.accessToken()}`,
                ...(method === "POST" && {"Content-Type": "application/json"}) },
            body });
        return response.ok ? (method === "POST" ? response : response.json()) :
            response.status === 404 ? null :
            console.error(`Error ${response.status} from ${url}`) || null;
    },
    /** Handle keydown events. */
    eventHandler(e) {
        if (e.key !== "Enter") return;
        e.stopPropagation();
        e.preventDefault();
        this.doSkip();
    }
};
introSkipper.setup();
