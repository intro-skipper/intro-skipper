# Intro Skipper

<div align="center">
    <p>
        <img alt="Plugin Banner" src="https://raw.githubusercontent.com/intro-skipper/intro-skipper/10.10/images/logo.png" />
    </p>
    <p>
        Analyzes the audio of television episodes to detect and skip over intros.
    </p>

[![CodeQL](https://github.com/intro-skipper/intro-skipper/actions/workflows/codeql.yml/badge.svg)](https://github.com/intro-skipper/intro-skipper/actions/workflows/codeql.yml)
<a href="https://github.com/intro-skipper/intro-skipper/releases">
<img alt="Total GitHub Downloads" src="https://img.shields.io/github/downloads/intro-skipper/intro-skipper/total?label=github%20downloads"/>
</a>
<br />
<p align="center">
  <a href="https://discord.gg/AYZ7RJ3BuA"><img src="https://invidget.switchblade.xyz/AYZ7RJ3BuA"></a>
</p>
</div>

## Manifest URL (All Jellyfin Versions)
> [!NOTE]
> If the plugin does not appear after adding the repository:
> * Check that you are using the latest Jellyfin version
> * Reload the plugin page without cache (`CTRL + F5` for Windows/Linux or `SHIFT + CMD + R` for macOS)

```
https://intro-skipper.org/manifest.json
```
**Important: This URL returns a manifest based on the Jellyfin version used to access it.
<br />
It will NOT return a manifest when viewed in a browser, as no Jellyfin version is provided.**

### As of Jellyfin 10.10, Intro Skipper does **NOT** modify the UI.

## Optional: File Transformation plugin

Some web UI features (for example, adjusting the skip-button timeout) require the File Transformation plugin. If it’s not installed, Intro Skipper will still work, but those enhancements won’t be applied.

<details>
<summary>Click here to see how to install the File Transformation plugin</summary>

- Plugin repo: https://github.com/IAmParadox27/jellyfin-plugin-file-transformation
- Easiest way to install:
    - Add as a plugin source repository to your Jellyfin server.
     ```
     https://www.iamparadox.dev/jellyfin/plugins/manifest.json
     ```
    - Find "File Transformation" in the Catalog and install it.
</details>

## System requirements

* Jellyfin 10.11.6 (or newer)
* Jellyfin's [fork](https://github.com/jellyfin/jellyfin-ffmpeg) of `ffmpeg` must be installed, version `7.1.1-7` or newer
  * `jellyfin/jellyfin` 10.11.z container: preinstalled
  * `linuxserver/jellyfin` 10.11.z container: preinstalled
  * Debian Linux based native installs: provided by the `jellyfin-ffmpeg7` package
  * MacOS native installs: build ffmpeg with chromaprint support ([instructions](https://github.com/intro-skipper/intro-skipper/wiki/Custom-FFMPEG-(MacOS)))
  * Gentoo Linux native installs: enable `xarblu-overlay` and install `media-video/jellyfin-ffmpeg`

## Policies
- [Code of conduct](https://github.com/intro-skipper/.github/blob/main/CODE_OF_CONDUCT.md)
- [Privacy policy](https://github.com/intro-skipper/.github/blob/main/PRIVACY.md)

## [Detection parameters](https://github.com/intro-skipper/intro-skipper/wiki#detection-parameters)

## [Detection types](https://github.com/intro-skipper/intro-skipper/wiki#detection-types)

## [Installation](https://github.com/intro-skipper/intro-skipper/wiki/Installation)
- #### [Install the plugin](https://github.com/intro-skipper/intro-skipper/wiki/Installation#step-1-install-the-plugin)
- #### [Verify the plugin](https://github.com/intro-skipper/intro-skipper/wiki/Installation#step-2-verify-the-plugin)
- #### [Custom FFMPEG (MacOS)](https://github.com/intro-skipper/intro-skipper/wiki/Custom-FFMPEG-(MacOS))

## [Jellyfin Skip Options](https://github.com/intro-skipper/intro-skipper/wiki/Jellyfin-Skip-Options)

## [Troubleshooting](https://github.com/intro-skipper/intro-skipper/wiki/Troubleshooting)
- #### [Plugin not shown in Catalog](https://github.com/intro-skipper/intro-skipper/wiki/Troubleshooting#plugin-not-shown-in-catalog)
- #### [Scheduled tasks fail instantly](https://github.com/intro-skipper/intro-skipper/wiki/Troubleshooting#scheduled-tasks-fail-instantly)
- #### [Skip button is not visible](https://github.com/intro-skipper/intro-skipper/wiki/Troubleshooting#skip-button-is-not-visible)

## Sponsors

Companies that kindly allow us to use their stuff:

| [DigitalOcean](https://www.digitalocean.com/?refcode=8471e96eb6dd)                                                                                                                                                                                                                           | [SignPath](https://signpath.org/)                                                                                  |
|-|-
| [![do_logo_vertical_blue svg](https://opensource.nyc3.cdn.digitaloceanspaces.com/attribution/assets/SVG/DO_Logo_horizontal_blue.svg)](https://www.digitalocean.com/) | [ ![Image](https://github.com/user-attachments/assets/2b5679e0-76a4-4ae7-bb37-a6a507a53466)](https://signpath.org/) |
| Hosting of various services                                                                                                                                                                                                                                               | Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).  
