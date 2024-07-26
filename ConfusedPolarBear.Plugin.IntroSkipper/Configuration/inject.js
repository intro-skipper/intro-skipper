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
        Object.assign(this, { allowEnter: true, skipSegments: {}, videoPlayer: null, skipButton: null, osdElement: null });
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
            this.skipSegments = await this.getJson(`Episode/${id}/IntroSkipperSegments`);
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
        if (location.startsWith('#/details')) {
            this.d('Attempting to inject Introskipper data');
            this.injectDetailsPage();
        }
        if (location !== "#/video") {
            if (this.videoPlayer) this.initializeState();
            return;
        }
        this.injectCss();
        this.injectButton();
        this.videoPlayer = document.querySelector("video");
        if (this.videoPlayer) {
            this.d("Hooking video timeupdate");
            this.videoPlayer.addEventListener("timeupdate", this.videoPositionChanged);
            this.osdElement = document.querySelector("div.videoOsdBottom")
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
        const config = await this.getJson("Intros/UserInterfaceConfiguration");
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
    async injectDetailsPage() {
        try {
            const visibleItemDetailPage = document.querySelector('div#itemDetailPage:not(.hide)');
            const detailPageContent = visibleItemDetailPage?.querySelector('.detailPageContent');
            if (!detailPageContent) {
                this.d('DetailPageContent not found');
                return;
            }
            const urlParams = new URLSearchParams(window.location.hash.split('?')[1]);
            const episodeId = urlParams.get('id');
            if (!episodeId) {
                this.d('Episode ID not found in URL');
                return;
            }
            if (detailPageContent.querySelector('#skipperCollapsible')) {
                this.d('Intro Skipper data already injected for this episode');
                return;
            }
            const skipperData = await this.getJson(`Episode/${episodeId}/IntroSkipperSegments`);
            if (!skipperData || Object.keys(skipperData).length === 0) {
                this.d('No intro skipper data found for this episode');
                return;
            }
            detailPageContent.appendChild(this.createSkipperDataElement(episodeId, skipperData));
            this.d('Intro Skipper Data injected');
        } catch (error) {
            console.error('Error during intro skipper data injection:', error);
        }
    },
    createSkipperDataElement(episodeId, skipperData) {
        const skipperCollapsible = document.createElement('div');
        skipperCollapsible.id = 'skipperCollapsible';
        skipperCollapsible.className = 'verticalSection detailVerticalSection verticalSection-extrabottompadding emby-scroller-container';

        const roundTime = time => Number(time).toFixed(2);
        const { Introduction, Credits } = skipperData;

        skipperCollapsible.innerHTML = `
            <div class="content-primary">
                <h2 class="sectionTitle sectionTitle-cards">Intro Skipper</h2>
                <div class="itemsContainer vertical-list">
                    <div class="listItem listItem-border">
                        <div class="listItemBody">
                            ${Introduction ? `<h3 class="listItemBodyText">Intro Start: ${roundTime(Introduction.IntroStart)}s, Intro End: ${roundTime(Introduction.IntroEnd)}s</h3>` : ''}
                            ${Credits ? `<h3 class="listItemBodyText">Credits Start: ${roundTime(Credits.IntroStart)}s, Credits End: ${roundTime(Credits.IntroEnd)}s</h3>` : ''}
                            <div class="listItemBodyText secondary" style="font-size: smaller; margin-top: 8px;">Episode ID: ${episodeId}</div>
                        </div>
                        <button is="emby-button" type="button" class="raised" id="editSkipperData">
                            <span>Edit Timestamps</span>
                        </button>
                    </div>
                </div>
            </div>
        `;
        skipperCollapsible.querySelector('#editSkipperData').addEventListener('click', () => this.editTimestamps(episodeId, skipperData));
        return skipperCollapsible;
    },
    editTimestamps(episodeId, skipperData) {
        this.d('Edit button clicked for episode:', episodeId);
        const dialog = document.createElement('div');
        dialog.className = 'dialogContainer';
        dialog.innerHTML = `
            <div class="focuscontainer dialog dialog-fixedSize dialog-small formDialog opened" data-history="true" modal="modal" data-autofocus="true" data-removeonclose="true" style="animation: 180ms ease-out both scaleup;">
                <div class="formDialogHeader">
                    <h3 class="formDialogHeaderTitle">Edit Timestamps</h3>
                    <button is="paper-icon-button-light" class="btnCancel btnClose autoSize paper-icon-button-light" tabindex="-1" title="Close" style="position: absolute; right: 0; top: 0;">
                        <span class="material-icons close" aria-hidden="true"></span>
                    </button>
                </div>
                <div class="formDialogContent smoothScrollY" style="padding-top:2em">
                    <form class="editTimestampsForm dialogContentInner dialog-content-centered">
                        ${['introStart', 'introEnd', 'creditsStart', 'creditsEnd'].map(id => `
                            <div class="inputContainer">
                                <label class="inputLabel inputLabelUnfocused" for="${id}">${id.charAt(0).toUpperCase() + id.slice(1).replace('Start', ' Start').replace('End', ' End')}</label>
                                <input is="emby-input" id="${id}" type="number" step="0.01" label="${id}" class="emby-input" value="${skipperData[id.startsWith('intro') ? 'Introduction' : 'Credits']?.[id.endsWith('Start') ? 'IntroStart' : 'IntroEnd'] ?? 0}">
                            </div>
                        `).join('')}
                    </form>
                </div>
                <div class="formDialogFooter">
                    <button is="emby-button" type="button" class="raised button-submit block btnSave formDialogFooterItem emby-button">
                        <span>Save</span>
                    </button>
                </div>
            </div>
        `;
        document.body.appendChild(dialog);
        dialog.querySelector('.btnSave').addEventListener('click', async () => {
            const newTimestamps = {
                Introduction: {
                    IntroStart: parseFloat(document.getElementById('introStart').value),
                    IntroEnd: parseFloat(document.getElementById('introEnd').value)
                },
                Credits: {
                    IntroStart: parseFloat(document.getElementById('creditsStart').value),
                    IntroEnd: parseFloat(document.getElementById('creditsEnd').value)
                }
            };
            try {
                const response = await this.fetchWithAuth(`Intros/Episode/${episodeId}/UpdateTimestamps`, "POST", JSON.stringify(newTimestamps));
                if (response.ok) {
                    this.d('Timestamps updated successfully');
                    const updatedSkipperData = await this.getJson(`Episode/${episodeId}/IntroSkipperSegments`);
                    const detailPageContent = document.querySelector('.detailPageContent');
                    detailPageContent.replaceChild(this.createSkipperDataElement(episodeId, updatedSkipperData), document.getElementById('skipperCollapsible'));
                } else {
                    console.error('Failed to update timestamps:', response.status, response.statusText);
                    alert('Failed to update timestamps. Please try again.');
                }
            } catch (error) {
                console.error('Error updating timestamps:', error);
                alert('An error occurred while updating timestamps. Please try again.');
            }
            document.body.removeChild(dialog);
        });
        dialog.querySelector('.btnCancel').addEventListener('click', () => document.body.removeChild(dialog));
    },
    /** Make an authenticated fetch to the Jellyfin server and parse the response body as JSON. */
    async getJson(url) {
        try {
            const response = await this.fetchWithAuth(url, "GET");
            return response.ok ? await response.json() : null;
        } catch (err) {
            console.error("Error fetching JSON:", err);
            return null;
        }
    },
    async fetchWithAuth(url, method, body) {
        const fullUrl = `${ApiClient.serverAddress()}/${url}`;
        const headers = {
            "Authorization": `MediaBrowser Token=${ApiClient.accessToken()}`
        };
        if (method === "POST") headers["Content-Type"] = "application/json";
        return await fetch(fullUrl, { method, headers, body });
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
