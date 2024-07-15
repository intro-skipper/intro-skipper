const introSkipper = {
    originalFetch: window.fetch.bind(window),
    d: msg => console.debug("[intro skipper] ", msg),
    setup() {
        this.initializeState();
        document.addEventListener("viewshow", this.viewShow.bind(this));
        window.fetch = this.fetchWrapper.bind(this);
        this.boundEventHandler = this.eventHandler.bind(this);
        this.d("Registered hooks");
        this.videoPositionChanged = this.throttle(this.videoPositionChanged.bind(this), 250);
        this.doSkip = this.throttle(this.doSkip.bind(this), 5000);
    },
    initializeState() {
        Object.assign(this, { allowEnter: true, skipSegments: {}, videoPlayer: null, skipButton: null});
    },
    /** Wrapper around fetch() that retrieves skip segments for the currently playing item. */
    async fetchWrapper(resource, options) {
        const response = await this.originalFetch(resource, options);
        try {
            const url = new URL(resource);
            if (!url.pathname.includes("/PlaybackInfo")) return response;
            this.d("Retrieving skip segments from URL", url.pathname);
            const pathArr = url.pathname.split("/");
            const id = pathArr[1] === "Items" ? pathArr[2] : pathArr[3];
            this.skipSegments = await this.secureFetch(`Episode/${id}/IntroSkipperSegments`);
            this.d("Successfully retrieved skip segments", this.skipSegments);
        } catch (e) {
            console.error("Unable to get skip segments from", resource, e);
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
        if (location !== "#/video") {
            if (this.videoPlayer) this.cleanup();
            return;
        }
        this.injectCss();
        this.injectButton();
        this.videoPlayer = document.querySelector("video");
        if (this.videoPlayer) {
            this.d("Hooking video timeupdate");
            this.videoPlayer.addEventListener("timeupdate", this.videoPositionChanged);
            document.body.addEventListener('keydown', this.boundEventHandler, true);
        }
    },
    cleanup() {
        this.d("Cleaning up intro skipper");
        this.videoPlayer.removeEventListener("timeupdate", this.videoPositionChanged);
        document.body.removeEventListener('keydown', this.boundEventHandler, true);
        if (this.skipButton) this.skipButton.remove();
        this.initializeState();
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
                --rounding: .2em;
                --accent: 0, 164, 220;
            }
            #skipIntro.upNextContainer {
                width: unset;
                margin: unset;
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
            #skipIntro.show .emby-button {
                opacity: 1;
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
    },
    /**
     * Inject the skip intro button into the video player.
     * Calling this function is a no-op if the CSS has already been injected.
     */
    async injectButton() {
        if (this.skipButton) {
            this.d("Button already exists");
            return;
        }
        const preExistingButton = document.querySelector("#skipIntro, .btnSkipIntro.injected");
        if (preExistingButton) {
            this.d("Removing existing button");
            preExistingButton.remove();
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
        this.skipButton.innerHTML = `
            <button is="emby-button" type="button" class="btnSkipIntro injected">
                <span id="btnSkipSegmentText"></span>
                <span class="material-icons skip_next"></span>
            </button>
        `;
        this.skipButton.dataset.Introduction = config.SkipButtonIntroText;
        this.skipButton.dataset.Credits = config.SkipButtonEndCreditsText;
        const controls = document.querySelector("div#videoOsdPage");
        controls.appendChild(this.skipButton);
    },
    /** Tests if the OSD controls are visible. */
    osdVisible() {
        const osd = document.querySelector("div.videoOsdBottom");
        return osd ? !osd.classList.contains("hide") : false;
    },
    /** Get the currently playing skippable segment. */
    getCurrentSegment(position) {
        for (const [key, segment] of Object.entries(this.skipSegments)) {
            if ((position >= segment.ShowSkipPromptAt && position < segment.HideSkipPromptAt) || 
                (this.osdVisible() && position >= segment.IntroStart && position < segment.IntroEnd)) {
                segment.SegmentType = key;
                return segment;
            }
        }
        return { SegmentType: "None" };
    },
    overrideBlur(embyButton) {
        if (!embyButton.originalBlur) {
            embyButton.originalBlur = embyButton.blur;
            embyButton.blur = () => {
                if (!embyButton.contains(document.activeElement)) {
                    embyButton.originalBlur();
                }
            };
        }
    },
    restoreBlur(embyButton) {
        if (embyButton.originalBlur) {
            embyButton.blur = embyButton.originalBlur;
            delete embyButton.originalBlur;
        }
    },
    throttle(func, limit) {
        let inThrottle;
        return (...args) => {
            if (!inThrottle) {
                func.apply(this, args);
                inThrottle = true;
                setTimeout(() => inThrottle = false, limit);
            }
        };
    },
    /** Playback position changed, check if the skip button needs to be displayed. */
    videoPositionChanged() {
        if (!this.allowEnter || !this.skipButton) return;
        const currentTime = this.videoPlayer.currentTime;
        if (currentTime === 0) return;
        const embyButton = this.skipButton.querySelector(".emby-button");
        const segmentType = introSkipper.getCurrentSegment(currentTime).SegmentType;
        if (segmentType === "None") {
            if (!this.skipButton.classList.contains('show')) return;
            this.skipButton.classList.remove('show');
            embyButton.addEventListener("transitionend", () => {
                this.skipButton.classList.add("hide");
                this.restoreBlur(embyButton);
                embyButton.blur();
            }, { once: true });
            return;
        }
        this.skipButton.querySelector("#btnSkipSegmentText").textContent = this.skipButton.dataset[segmentType];
        if (!this.skipButton.classList.contains("hide")) {
            if (!this.osdVisible() && !embyButton.contains(document.activeElement)) embyButton.focus({ focusVisible: true });
            return;
        }
        requestAnimationFrame(() => {
            this.skipButton.classList.remove("hide");
            requestAnimationFrame(() => {
                this.skipButton.classList.add('show');
                this.overrideBlur(embyButton);
                embyButton.focus({ focusVisible: true });
            });
        });
    },
    /** Seeks to the end of the intro. */
    async doSkip() {
        this.d("Skipping intro");
        const segment = this.getCurrentSegment(this.videoPlayer.currentTime);
        if (segment.SegmentType === "None") {
            console.warn("[intro skipper] doSkip() called without an active segment");
            return;
        }
        this.allowEnter = false;
        // Check if the segment is "Credits" and skipping would leave less than 2 seconds of video
        if (segment.SegmentType === "Credits" && this.videoPlayer.duration - segment.IntroEnd < 3) {
            const nextEpisodeLoaded = new Promise(resolve => {
                const onLoadStart = () => {
                    this.videoPlayer.removeEventListener('loadstart', onLoadStart);
                    resolve(true);
                };
                this.videoPlayer.addEventListener('loadstart', onLoadStart);
                setTimeout(() => {
                    this.videoPlayer.removeEventListener('loadstart', onLoadStart);
                    resolve(false);
                }, 700);
            });
            // Simulate 'N' key press to go to the next episode
            document.dispatchEvent(new KeyboardEvent('keydown', { key: 'N', shiftKey: true, bubbles: true }));
            if (await nextEpisodeLoaded) {
                this.allowEnter = true;
                return;
            }
        }
        await new Promise(resolve => {
            const onSeeked = () => {
                this.videoPlayer.removeEventListener('seeked', onSeeked);
                resolve();
            };
            this.videoPlayer.addEventListener('seeked', onSeeked);
            this.videoPlayer.currentTime = segment.IntroEnd;
        });
        this.allowEnter = true;
    },
    /** Make an authenticated fetch to the Jellyfin server and parse the response body as JSON. */
    async secureFetch(url) {
        url = new URL(url, ApiClient.serverAddress());
        const res = await fetch(url, { headers: { "Authorization": `MediaBrowser Token=${ApiClient.accessToken()}` } });
        if (!res.ok) throw new Error(`Expected status 200 from ${url}, but got ${res.status}`);
        return res.json();
    },
    /** Handle keydown events. */
    eventHandler(e) {
        if (e.key === "Enter" && this.allowEnter && this.skipButton.querySelector(".emby-button").contains(document.activeElement)) e.stopPropagation();
    }
};
introSkipper.setup();
