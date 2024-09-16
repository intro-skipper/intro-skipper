# Intro Skipper (beta)

<div align="center">
    <p>
        <img alt="Plugin Banner" src="https://raw.githubusercontent.com/jumoog/intro-skipper/master/images/logo.png" />
    </p>
    <p>
        Analyzes the audio of television episodes to detect and skip over intros.
    </p>
    
[![CodeQL](https://github.com/jumoog/intro-skipper/actions/workflows/codeql.yml/badge.svg)](https://github.com/jumoog/intro-skipper/actions/workflows/codeql.yml)


`https://raw.githubusercontent.com/jumoog/intro-skipper/master/manifest.json`
</div>

## Jellyfin 10.8
👉👉👉 [Jellyfin 10.8 Instructions](https://github.com/jumoog/intro-skipper/blob/10.8/README.md)

## System requirements

* Jellyfin 10.9.11 (or newer)
* Jellyfin's [fork](https://github.com/jellyfin/jellyfin-ffmpeg) of `ffmpeg` must be installed, version `6.0.1-5` or newer
  * `jellyfin/jellyfin` 10.9.z container: preinstalled
  * `linuxserver/jellyfin` 10.9.z container: preinstalled
  * Debian Linux based native installs: provided by the `jellyfin-ffmpeg6` package
  * MacOS native installs: build ffmpeg with chromaprint support ([instructions](https://github.com/jumoog/intro-skipper/wiki/Custom-FFMPEG-(MacOS)))

## Detection parameters

Show introductions will be detected if they are:

* Located within the first 25% of an episode or the first 10 minutes, whichever is smaller
* Between 15 seconds and 2 minutes long

Ending credits will be detected if they are shorter than 4 minutes.

These parameters can be configured by opening the plugin settings

## [Installation](https://github.com/jumoog/intro-skipper/wiki/Installation)
- #### [Install the plugin](https://github.com/jumoog/intro-skipper/wiki/Installation#step-1-install-the-plugin)
- #### [Configure the plugin](https://github.com/jumoog/intro-skipper/wiki/Installation#step-2-configure-the-plugin)
- #### [Custom FFMPEG (MacOS)](https://github.com/jumoog/intro-skipper/wiki/Custom-FFMPEG-(MacOS))

## [Troubleshooting](https://github.com/jumoog/intro-skipper/wiki/Troubleshooting)
- #### [Scheduled tasks fail instantly](https://github.com/jumoog/intro-skipper/wiki/Troubleshooting#scheduled-tasks-fail-instantly)

- #### [Skip button is not visible](https://github.com/jumoog/intro-skipper/wiki/Troubleshooting#skip-button-is-not-visible)

## [API Documentation](https://github.com/jumoog/intro-skipper/blob/master/docs/api.md)

Documentation about how the API works can be found in [api.md](docs/api.md).
