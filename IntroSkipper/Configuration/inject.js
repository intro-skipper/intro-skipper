const introSkipper = {
	originalFetch: window.fetch.bind(window),
	originalXHROpen: XMLHttpRequest.prototype.open,
	d: (msg) => console.debug("[intro skipper] ", msg),
	setup() {
		const self = this;
		this.initializeState();
		this.initializeObserver();
		this.currentOption =
			localStorage.getItem("introskipperOption") || "Show Button";
		window.fetch = this.fetchWrapper.bind(this);
		XMLHttpRequest.prototype.open = function (...args) {
			self.xhrOpenWrapper(this, ...args);
		};
		document.addEventListener("viewshow", this.viewShow.bind(this));
		this.videoPositionChanged = this.videoPositionChanged.bind(this);
		this.handleEscapeKey = this.handleEscapeKey.bind(this);
		this.d("Registered hooks");
	},
	initializeState() {
		Object.assign(this, {
			allowEnter: true,
			skipSegments: {},
			videoPlayer: null,
			skipButton: null,
			osdElement: null,
			skipperData: null,
			currentEpisodeId: null,
			injectMetadata: false,
		});
	},
	initializeObserver() {
		this.observer = new MutationObserver((mutations) => {
			const actionSheet =
				mutations[mutations.length - 1].target.querySelector(".actionSheet");
			if (
				actionSheet &&
				!actionSheet.querySelector(`[data-id="${"introskipperMenu"}"]`)
			)
				this.injectIntroSkipperOptions(actionSheet);
		});
	},
	fetchWrapper(resource, options) {
		const response = this.originalFetch(resource, options);
		const url = new URL(resource);
		if (this.injectMetadata && url.pathname.includes("/MetadataEditor")) {
			this.processMetadata(url.pathname);
		}
		return response;
	},
	xhrOpenWrapper(xhr, method, url, ...rest) {
		url.includes("/PlaybackInfo") && this.processPlaybackInfo(url);
		return this.originalXHROpen.apply(xhr, [method, url, ...rest]);
	},
	async processPlaybackInfo(url) {
		const id = this.extractId(url);
		if (id) {
			try {
				this.skipSegments = await this.secureFetch(
					`Episode/${id}/IntroSkipperSegments`,
				);
			} catch (error) {
				this.d(`Error fetching skip segments: ${error.message}`);
			}
		}
	},
	async processMetadata(url) {
		const id = this.extractId(url);
		if (id) {
			try {
				this.skipperData = await this.secureFetch(`Episode/${id}/Timestamps`);
				if (this.skipperData) {
					this.currentEpisodeId = id;
					requestAnimationFrame(() => {
						const metadataFormFields = document.querySelector(
							".metadataFormFields",
						);
						metadataFormFields && this.injectSkipperFields(metadataFormFields);
					});
				}
			} catch (e) {
				console.error("Error processing", e);
			}
		}
	},
	extractId(searchString) {
		const startIndex = searchString.indexOf("Items/") + 6;
		const endIndex = searchString.indexOf("/", startIndex);
		return endIndex !== -1
			? searchString.substring(startIndex, endIndex)
			: searchString.substring(startIndex);
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
				this.videoPlayer.addEventListener(
					"timeupdate",
					this.videoPositionChanged,
				);
				this.osdElement = document.querySelector("div.videoOsdBottom");
				this.observer.observe(document.body, {
					childList: true,
					subtree: false,
				});
			}
		} else {
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
		return this.osdElement
			? !this.osdElement.classList.contains("hide")
			: false;
	},
	/** Get the currently playing skippable segment. */
	getCurrentSegment(position) {
		for (const key in this.skipSegments) {
			const segment = this.skipSegments[key];
			if (
				(position > segment.ShowSkipPromptAt &&
					position < segment.HideSkipPromptAt) ||
				(this.osdVisible() &&
					position > segment.IntroStart &&
					position < segment.IntroEnd - 3)
			) {
				segment["SegmentType"] = key;
				return segment;
			}
		}
		return { SegmentType: "None" };
	},
	overrideBlur(button) {
		if (!button.originalBlur) {
			button.originalBlur = button.blur;
			button.blur = function () {
				if (!this.contains(document.activeElement)) {
					this.originalBlur();
				}
			};
		}
	},
	/** Playback position changed, check if the skip button needs to be displayed. */
	videoPositionChanged() {
		if (!this.skipButton || !this.allowEnter) return;
		const { SegmentType: segmentType } = this.getCurrentSegment(this.videoPlayer.currentTime);
		if (
			segmentType === "None" ||
			this.currentOption === "Off"
		) {
			this.hideSkipButton();
			return;
		}
		if (
			this.currentOption === "Automatically Skip" ||
			(this.currentOption === "Button w/ auto PiP" &&
				document.pictureInPictureElement)
		) {
			this.doSkip();
			return;
		}
        const button = this.skipButton.querySelector(".emby-button");
		this.skipButton.querySelector("#btnSkipSegmentText").textContent =
			this.skipButton.dataset[segmentType];
		if (!this.skipButton.classList.contains("hide")) {
			if (!this.osdVisible() && !button.contains(document.activeElement)) {
                button.focus();
            }
            return;
		}
		requestAnimationFrame(() => {
			this.skipButton.classList.remove("hide");
			requestAnimationFrame(() => {
				this.skipButton.classList.add("show");
				this.overrideBlur(button);
				button.focus();
			});
		});
	},
    hideSkipButton() {
        if (this.skipButton.classList.contains("show")) {
            this.skipButton.classList.remove("show");
            const button = this.skipButton.querySelector(".emby-button");
            button.addEventListener(
                "transitionend",
                () => {
                    this.skipButton.classList.add("hide");
                    if (this.osdVisible()) {
                        this.osdElement.querySelector("button.btnPause").focus();
                    } else {
                        button.originalBlur();
                    }
                },
                { once: true },
            );
        }
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
		const seekedHandler = () => {
			this.videoPlayer.removeEventListener("seeked", seekedHandler);
			setTimeout(() => {
				this.allowEnter = true;
			}, 700);
		};
		this.videoPlayer.addEventListener("seeked", seekedHandler);
		this.videoPlayer.currentTime = segment.IntroEnd;
        this.hideSkipButton();
	},
	createButton(ref, id, innerHTML, clickHandler) {
		const button = ref.cloneNode(true);
		button.setAttribute("data-id", id);
		button.innerHTML = innerHTML;
		button.addEventListener("click", clickHandler);
		return button;
	},
	closeSubmenu(fullscreen) {
		document.querySelector(".dialogContainer").remove();
		document.querySelector(".dialogBackdrop").remove();
		document.dispatchEvent(new KeyboardEvent("keydown", { key: "Control" }));
		if (!fullscreen) return;
		document.removeEventListener("keydown", this.handleEscapeKey);
		document.querySelector(".btnVideoOsdSettings").focus();
	},
	openSubmenu(ref, menu) {
		const options = [
			"Show Button",
			"Button w/ auto PiP",
			"Automatically Skip",
			"Off",
		];
		const submenu = menu.cloneNode(true);
		const scroller = submenu.querySelector(".actionSheetScroller");
		scroller.innerHTML = "";
		for (const option of options) {
			if (option !== "Button w/ auto PiP" || document.pictureInPictureEnabled) {
				const button = this.createButton(
					ref,
					`introskipper-${option.toLowerCase().replace(" ", "-")}`,
					`<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons check" aria-hidden="true" style="visibility:${option === this.currentOption ? "visible" : "hidden"};"></span><div class="listItemBody actionsheetListItemBody"><div class="listItemBodyText actionSheetItemText">${option}</div></div>`,
					() => this.selectOption(option),
				);
				scroller.appendChild(button);
			}
		}
		const backdrop = document.createElement("div");
		backdrop.className = "dialogBackdrop dialogBackdropOpened";
		document.body.append(backdrop, submenu);
		const actionSheet = submenu.querySelector(".actionSheet");
		if (actionSheet.classList.contains("actionsheet-not-fullscreen")) {
			this.adjustPosition(
				actionSheet,
				document.querySelector(".btnVideoOsdSettings"),
			);
			submenu.addEventListener("click", () => this.closeSubmenu(false));
		} else {
			submenu
				.querySelector(".btnCloseActionSheet")
				.addEventListener("click", () => this.closeSubmenu(true));
			scroller.addEventListener("click", () => this.closeSubmenu(true));
			document.addEventListener("keydown", this.handleEscapeKey);
			setTimeout(() => scroller.firstElementChild.focus(), 240);
		}
	},
	selectOption(option) {
		this.currentOption = option;
		localStorage.setItem("introskipperOption", option);
		this.d(`Introskipper option selected and saved: ${option}`);
	},
	isAutoSkipLocked(config) {
		const isAutoSkip = config.AutoSkip && config.AutoSkipCredits;
		const isAutoSkipClient = new Set(config.ClientList.split(",")).has(
			ApiClient.appName(),
		);
		return isAutoSkip || (config.SkipButtonVisible && isAutoSkipClient);
	},
	async injectIntroSkipperOptions(actionSheet) {
		if (!this.skipButton) return;
		const config = await this.secureFetch("Intros/UserInterfaceConfiguration");
		if (this.isAutoSkipLocked(config)) {
			this.d("Auto skip enforced by server");
			return;
		}
		const statsButton = actionSheet.querySelector('[data-id="stats"]');
		if (!statsButton) return;
		const menuItem = this.createButton(
			statsButton,
			"introskipperMenu",
			`<div class="listItemBody actionsheetListItemBody"><div class="listItemBodyText actionSheetItemText">Intro Skipper</div></div><div class="listItemAside actionSheetItemAsideText">${this.currentOption}</div>`,
			() =>
				this.openSubmenu(statsButton, actionSheet.closest(".dialogContainer")),
		);
		const originalWidth = actionSheet.offsetWidth;
		statsButton.before(menuItem);
		if (actionSheet.classList.contains("actionsheet-not-fullscreen"))
			this.adjustPosition(actionSheet, menuItem, originalWidth);
	},
	adjustPosition(element, reference, originalWidth) {
		if (originalWidth) {
			const currentTop = Number.parseInt(element.style.top, 10) || 0;
			element.style.top = `${currentTop - reference.offsetHeight}px`;
			const newWidth = Math.max(reference.offsetWidth - originalWidth, 0);
			const originalLeft = Number.parseInt(element.style.left, 10) || 0;
			element.style.left = `${originalLeft - newWidth / 2}px`;
		} else {
			const rect = reference.getBoundingClientRect();
			element.style.left = `${Math.min(rect.left - (element.offsetWidth - rect.width) / 2, window.innerWidth - element.offsetWidth - 10)}px`;
			element.style.top = `${rect.top - element.offsetHeight + rect.height}px`;
		}
	},
	injectSkipperFields(metadataFormFields) {
		const skipperFields = document.createElement("div");
		skipperFields.className = "detailSection introskipperSection";
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
		metadataFormFields
			.querySelector("#metadataSettingsCollapsible")
			.insertAdjacentElement("afterend", skipperFields);
		this.attachSaveListener(metadataFormFields);
		this.updateSkipperFields(skipperFields);
		this.setTimeInputs(skipperFields);
	},
	updateSkipperFields(skipperFields) {
		const { Introduction = {}, Credits = {} } = this.skipperData;
		skipperFields.querySelector("#introStartEdit").value =
			Introduction.Start || 0;
		skipperFields.querySelector("#introEndEdit").value = Introduction.End || 0;
		skipperFields.querySelector("#creditsStartEdit").value = Credits.Start || 0;
		skipperFields.querySelector("#creditsEndEdit").value = Credits.End || 0;
	},
	attachSaveListener(metadataFormFields) {
		const saveButton = metadataFormFields.querySelector(
			".formDialogFooter .btnSave",
		);
		if (saveButton) {
			saveButton.addEventListener("click", this.saveSkipperData.bind(this));
		} else {
			console.error("Save button not found");
		}
	},
	setTimeInputs(skipperFields) {
		const inputContainers = skipperFields.querySelectorAll(".inputContainer");
		for (const container of inputContainers) {
			const displayInput = container.querySelector('[id$="Display"]');
			const editInput = container.querySelector('[id$="Edit"]');
			displayInput.addEventListener("pointerdown", (e) => {
				e.preventDefault();
				this.switchToEdit(displayInput, editInput);
			});
			editInput.addEventListener("blur", () =>
				this.switchToDisplay(displayInput, editInput),
			);
			displayInput.value = this.formatTime(
				Number.parseFloat(editInput.value) || 0,
			);
		}
	},
	formatTime(totalSeconds) {
		if (!totalSeconds) return "0 seconds";
		const totalRoundedSeconds = Math.round(totalSeconds);
		const hours = Math.floor(totalRoundedSeconds / 3600);
		const minutes = Math.floor((totalRoundedSeconds % 3600) / 60);
		const seconds = totalRoundedSeconds % 60;
		const result = [];
		if (hours) result.push(`${hours} hour${hours !== 1 ? "s" : ""}`);
		if (minutes) result.push(`${minutes} minute${minutes !== 1 ? "s" : ""}`);
		if (seconds || !result.length)
			result.push(`${seconds} second${seconds !== 1 ? "s" : ""}`);
		return result.join(" ");
	},
	switchToEdit(displayInput, editInput) {
		displayInput.style.display = "none";
		editInput.style.display = "";
		editInput.focus();
	},
	switchToDisplay(displayInput, editInput) {
		editInput.style.display = "none";
		displayInput.style.display = "";
		displayInput.value = this.formatTime(
			Number.parseFloat(editInput.value) || 0,
		);
	},
	async saveSkipperData() {
		const newTimestamps = {
			Introduction: {
				Start: Number.parseFloat(
					document.getElementById("introStartEdit").value || 0,
				),
				End: Number.parseFloat(
					document.getElementById("introEndEdit").value || 0,
				),
			},
			Credits: {
				Start: Number.parseFloat(
					document.getElementById("creditsStartEdit").value || 0,
				),
				End: Number.parseFloat(
					document.getElementById("creditsEndEdit").value || 0,
				),
			},
		};
		const { Introduction = {}, Credits = {} } = this.skipperData;
		if (
			newTimestamps.Introduction.Start !== (Introduction.Start || 0) ||
			newTimestamps.Introduction.End !== (Introduction.End || 0) ||
			newTimestamps.Credits.Start !== (Credits.Start || 0) ||
			newTimestamps.Credits.End !== (Credits.End || 0)
		) {
			const response = await this.secureFetch(
				`Episode/${this.currentEpisodeId}/Timestamps`,
				"POST",
				JSON.stringify(newTimestamps),
			);
			this.d(
				response.ok
					? "Timestamps updated successfully"
					: "Failed to update timestamps:",
				response.status,
			);
		} else {
			this.d("Timestamps have not changed, skipping update");
		}
	},
	/** Make an authenticated fetch to the Jellyfin server and parse the response body as JSON. */
	async secureFetch(url, method = "GET", body = null) {
		const response = await fetch(`${ApiClient.serverAddress()}/${url}`, {
			method,
			headers: Object.assign(
				{ Authorization: `MediaBrowser Token=${ApiClient.accessToken()}` },
				method === "POST" ? { "Content-Type": "application/json" } : {},
			),
			body,
		});
		return response.ok
			? method === "POST"
				? response
				: response.json()
			: response.status === 404
				? null
				: console.error(`Error ${response.status} from ${url}`) || null;
	},
	/** Handle keydown events. */
	eventHandler(e) {
		if (e.key !== "Enter") return;
		e.stopPropagation();
		e.preventDefault();
		this.doSkip();
	},
	handleEscapeKey(e) {
		if (e.key === "Escape" || e.keyCode === 461 || e.keyCode === 10009) {
			e.stopPropagation();
			this.closeSubmenu(true);
		}
	},
};
introSkipper.setup();
