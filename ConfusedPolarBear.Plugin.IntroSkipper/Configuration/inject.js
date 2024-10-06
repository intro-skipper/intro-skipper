const introSkipper = {
    originalFetch: window.fetch.bind(window),
    d: msg => console.debug("[intro skipper] ", msg),
    setup() {
        this.initializeState();
        this.initializeObserver();
        this.currentOption = localStorage.getItem('introskipperOption') || 'Show Button';
        document.addEventListener("viewshow", this.viewShow.bind(this));
        window.fetch = this.fetchWrapper.bind(this);
        this.videoPositionChanged = this.videoPositionChanged.bind(this);
        this.handleEscapeKey = this.handleEscapeKey.bind(this);
        this.d("Registered hooks");
    },
    initializeState() {
        Object.assign(this, { allowEnter: true, skipSegments: {}, videoPlayer: null, skipButton: null, osdElement: null, skipperData: null, currentEpisodeId: null, injectMetadata: false });
    },
    initializeObserver() {
        this.observer = new MutationObserver(mutations => {
            const actionSheet = mutations[mutations.length - 1].target.querySelector('.actionSheet');
            if (actionSheet && !actionSheet.querySelector(`[data-id="${'introskipperMenu'}"]`)) this.injectIntroSkipperOptions(actionSheet);
        });
    },
    /** Wrapper around fetch() that retrieves skip segments for the currently playing item or metadata. */
    async fetchWrapper(resource, options) {
        const response = await this.originalFetch(resource, options);
        this.processResource(resource);
        return response;
    },
    async processResource(resource) {
        try {
            const url = new URL(resource);
            const pathname = url.pathname;
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
                    requestAnimationFrame(() => {
                        const metadataFormFields = document.querySelector('.metadataFormFields');
                        metadataFormFields && this.injectSkipperFields(metadataFormFields);
                    });
                }
            }
        } catch (e) {
            console.error("Error processing", resource, e);
        }
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
                this.observer.observe(document.body, { childList: true, subtree: false });
            }
        }
        else {
            this.observer.disconnect();
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
            #skipIntro .emby-button:focus {
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
            button.blur = function() {
                if (!this.contains(document.activeElement)) {
                    this.originalBlur();
                }
            };
        }
    },
    /** Playback position changed, check if the skip button needs to be displayed. */
    videoPositionChanged() {
        if (!this.skipButton) return;
        const embyButton = this.skipButton.querySelector(".emby-button");
        const segmentType = this.getCurrentSegment(this.videoPlayer.currentTime).SegmentType;
        if (segmentType === "None" || this.currentOption === "Off" || !this.allowEnter) {
            if (this.skipButton.classList.contains('show')) {
                this.skipButton.classList.remove('show');
                embyButton.addEventListener("transitionend", () => {
                    this.skipButton.classList.add("hide");
                    if (this.osdVisible()) {
                        this.osdElement.querySelector('button.btnPause').focus();
                    } else {
                        embyButton.originalBlur();
                    }
                }, { once: true });
            }
            return;
        }
        if (this.currentOption === "Automatically Skip" || (this.currentOption === "Button w/ auto PiP" && document.pictureInPictureElement)) {
            this.doSkip();
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
        const seekedHandler = () => {
            this.videoPlayer.removeEventListener('seeked', seekedHandler);
            setTimeout(() => {
                this.allowEnter = true;
            }, 500);
        };
        this.videoPlayer.addEventListener('seeked', seekedHandler);
        this.videoPlayer.currentTime = segment.SegmentType === "Credits" && this.videoPlayer.duration - segment.IntroEnd < 3
            ? this.videoPlayer.duration + 10
            : segment.IntroEnd;
    },
    createButton(ref, id, innerHTML, clickHandler) {
        const button = ref.cloneNode(true);
        button.setAttribute('data-id', id);
        button.innerHTML = innerHTML;
        button.addEventListener('click', clickHandler);
        return button;
    },
    closeSubmenu(fullscreen) {
        document.querySelector('.dialogContainer').remove();
        document.querySelector('.dialogBackdrop').remove()
        document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Control' }));
        if (!fullscreen) return;
        document.removeEventListener('keydown', this.handleEscapeKey);
        document.querySelector('.btnVideoOsdSettings').focus();
    },
    openSubmenu(ref, menu) {
        const options = ['Show Button', 'Button w/ auto PiP', 'Automatically Skip', 'Off'];
        const submenu = menu.cloneNode(true);
        const scroller = submenu.querySelector('.actionSheetScroller');
        scroller.innerHTML = '';
        options.forEach(option => {
            if (option !== 'Button w/ auto PiP' || document.pictureInPictureEnabled) {
                const button = this.createButton(ref, `introskipper-${option.toLowerCase().replace(' ', '-')}`,
                                            `<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons check" aria-hidden="true" style="visibility:${option === this.currentOption ? 'visible' : 'hidden'};"></span><div class="listItemBody actionsheetListItemBody"><div class="listItemBodyText actionSheetItemText">${option}</div></div>`,
                                            () => this.selectOption(option));
                scroller.appendChild(button);
            }
        });
        const backdrop = document.createElement('div');
        backdrop.className = 'dialogBackdrop dialogBackdropOpened';
        document.body.append(backdrop, submenu);
        const actionSheet = submenu.querySelector('.actionSheet');
        if (actionSheet.classList.contains('actionsheet-not-fullscreen')) {
            this.adjustPosition(actionSheet, document.querySelector('.btnVideoOsdSettings'));
            submenu.addEventListener('click', () => this.closeSubmenu(false));
        } else {
            submenu.querySelector('.btnCloseActionSheet').addEventListener('click', () => this.closeSubmenu(true))
            scroller.addEventListener('click', () => this.closeSubmenu(true))
            document.addEventListener('keydown', this.handleEscapeKey);
            setTimeout(() => scroller.firstElementChild.focus(), 240);
        }
    },
    selectOption(option) {
        this.currentOption = option;
        localStorage.setItem('introskipperOption', option);
        this.d(`Introskipper option selected and saved: ${option}`);
    },
    injectIntroSkipperOptions(actionSheet) {
        if (!this.skipButton) return;
        const statsButton = actionSheet.querySelector('[data-id="stats"]');
        if (!statsButton) return;
        const menuItem = this.createButton(statsButton, 'introskipperMenu',
                                      `<div class="listItemBody actionsheetListItemBody"><div class="listItemBodyText actionSheetItemText">Intro Skipper</div></div><div class="listItemAside actionSheetItemAsideText">${this.currentOption}</div>`,
                                      () => this.openSubmenu(statsButton, actionSheet.closest('.dialogContainer')));
        const originalWidth = actionSheet.offsetWidth;
        statsButton.before(menuItem);
        if (actionSheet.classList.contains('actionsheet-not-fullscreen')) this.adjustPosition(actionSheet, menuItem, originalWidth);
    },
    adjustPosition(element, reference, originalWidth) {
        if (originalWidth) {
            const currentTop = parseInt(element.style.top, 10) || 0;
            element.style.top = `${currentTop - reference.offsetHeight}px`;
            const newWidth = Math.max(reference.offsetWidth - originalWidth, 0);
            const originalLeft = parseInt(element.style.left, 10) || 0;
            element.style.left = `${originalLeft - newWidth / 2}px`;
        } else {
            const rect = reference.getBoundingClientRect();
            element.style.left = `${Math.min(rect.left - (element.offsetWidth - rect.width) / 2, window.innerWidth - element.offsetWidth - 10)}px`;
            element.style.top = `${rect.top - element.offsetHeight + rect.height}px`;
        }
    },
    injectSkipperFields(metadataFormFields) {
        const skipperFields = document.createElement('div');
        skipperFields.className = 'detailSection introskipperSection';
        skipperFields.innerHTML = `
            <h2>Intro Skipper</h2>
            <div class="inlineForm">
                <div class="inputContainer">
                    <label class="inputLabel inputLabelUnfocused" for="introStart">Intro Start</label>
                    <input type="text" id="introStartDisplay" class="emby-input custom-time-input" readonly>
                    <input type="number" id="introStartEdit" class="emby-input custom-time-input" style="display: none;" step="any" min="0">
                </div>
                <div class="inputContainer">
                    <label class="inputLabel inputLabelUnfocused" for="introEnd">Intro End</label>
                    <input type="text" id="introEndDisplay" class="emby-input custom-time-input" readonly>
                    <input type="number" id="introEndEdit" class="emby-input custom-time-input" style="display: none;" step="any" min="0">
                </div>
            </div>
            <div class="inlineForm">
                <div class="inputContainer">
                    <label class="inputLabel inputLabelUnfocused" for="creditsStart">Credits Start</label>
                    <input type="text" id="creditsStartDisplay" class="emby-input custom-time-input" readonly>
                    <input type="number" id="creditsStartEdit" class="emby-input custom-time-input" style="display: none;" step="any" min="0">
                </div>
                <div class="inputContainer">
                    <label class="inputLabel inputLabelUnfocused" for="creditsEnd">Credits End</label>
                    <input type="text" id="creditsEndDisplay" class="emby-input custom-time-input" readonly>
                    <input type="number" id="creditsEndEdit" class="emby-input custom-time-input" style="display: none;" step="any" min="0">
                </div>
            </div>
        `;
        metadataFormFields.querySelector('#metadataSettingsCollapsible').insertAdjacentElement('afterend', skipperFields);
        this.attachSaveListener(metadataFormFields);
        this.updateSkipperFields(skipperFields);
        this.setTimeInputs(skipperFields);
    },
    updateSkipperFields(skipperFields) {
        const { Introduction = {}, Credits = {} } = this.skipperData;
        skipperFields.querySelector('#introStartEdit').value = Introduction.Start || 0;
        skipperFields.querySelector('#introEndEdit').value = Introduction.End || 0;
        skipperFields.querySelector('#creditsStartEdit').value = Credits.Start || 0;
        skipperFields.querySelector('#creditsEndEdit').value = Credits.End || 0;
    },
    attachSaveListener(metadataFormFields) {
        const saveButton = metadataFormFields.querySelector('.formDialogFooter .btnSave');
        if (saveButton) {
            saveButton.addEventListener('click', this.saveSkipperData.bind(this));
        } else {
            console.error('Save button not found');
        }
    },
    setTimeInputs(skipperFields) {
        const inputContainers = skipperFields.querySelectorAll('.inputContainer');
        inputContainers.forEach(container => {
            const displayInput = container.querySelector('[id$="Display"]');
            const editInput = container.querySelector('[id$="Edit"]');
            displayInput.addEventListener('pointerdown', (e) => {
                e.preventDefault();
                this.switchToEdit(displayInput, editInput);
            });
            editInput.addEventListener('blur', () => this.switchToDisplay(displayInput, editInput));
            displayInput.value = this.formatTime(parseFloat(editInput.value) || 0);
        });
    },
    formatTime(totalSeconds) {
        const totalRoundedSeconds = Math.round(totalSeconds);
        const hours = Math.floor(totalRoundedSeconds / 3600);
        const minutes = Math.floor((totalRoundedSeconds % 3600) / 60);
        const seconds = totalRoundedSeconds % 60;
        let result = [];
        if (hours > 0) result.push(`${hours} hour${hours !== 1 ? 's' : ''}`);
        if (minutes > 0) result.push(`${minutes} minute${minutes !== 1 ? 's' : ''}`);
        if (seconds > 0 || result.length === 0) result.push(`${seconds} second${seconds !== 1 ? 's' : ''}`);
        return result.join(' ');
    },
    switchToEdit(displayInput, editInput) {
        displayInput.style.display = 'none';
        editInput.style.display = '';
        editInput.focus();
    },
    switchToDisplay(displayInput, editInput) {
        editInput.style.display = 'none';
        displayInput.style.display = '';
        displayInput.value = this.formatTime(parseFloat(editInput.value) || 0);
    },
    async saveSkipperData() {
        const newTimestamps = {
            Introduction: {
                Start: parseFloat(document.getElementById('introStartEdit').value || 0),
                End: parseFloat(document.getElementById('introEndEdit').value || 0)
            },
            Credits: {
                Start: parseFloat(document.getElementById('creditsStartEdit').value || 0),
                End: parseFloat(document.getElementById('creditsEndEdit').value || 0)
            }
        };
        const { Introduction = {}, Credits = {} } = this.skipperData;
        if (newTimestamps.Introduction.Start !== (Introduction.Start || 0) ||
            newTimestamps.Introduction.End !== (Introduction.End || 0) ||
            newTimestamps.Credits.Start !== (Credits.Start || 0) ||
            newTimestamps.Credits.End !== (Credits.End || 0)) {
            const response = await this.secureFetch(`Episode/${this.currentEpisodeId}/Timestamps`, "POST", JSON.stringify(newTimestamps));
            this.d(response.ok ? 'Timestamps updated successfully' : 'Failed to update timestamps:', response.status);
        } else {
            this.d('Timestamps have not changed, skipping update');
        }
    },
    /** Make an authenticated fetch to the Jellyfin server and parse the response body as JSON. */
    async secureFetch(url, method = "GET", body = null) {
        const response = await fetch(`${ApiClient.serverAddress()}/${url}`, {
            method,
            headers: Object.assign({ "Authorization": `MediaBrowser Token=${ApiClient.accessToken()}` },
                method === "POST" ? {"Content-Type": "application/json"} : {}),
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
    },
    handleEscapeKey(e) {
        if (e.key === 'Escape' || e.keyCode === 461 || e.keyCode === 10009) {
            e.stopPropagation();
            this.closeSubmenu(true);
        }
    }
};
introSkipper.setup();
