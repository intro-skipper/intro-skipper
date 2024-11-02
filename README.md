# Intro Skipper

<div align="center">
    <p>
        <img alt="Plugin Banner" src="https://raw.githubusercontent.com/intro-skipper/intro-skipper/master/images/logo.png" />
    </p>
    <p>
        Analyzes the audio of television episodes to detect and skip over intros.
    </p>

[![CodeQL](https://github.com/intro-skipper/intro-skipper/actions/workflows/codeql.yml/badge.svg)](https://github.com/intro-skipper/intro-skipper/actions/workflows/codeql.yml)
<a href="https://github.com/intro-skipper/intro-skipper/releases">
<img alt="Total GitHub Downloads" src="https://img.shields.io/github/downloads/intro-skipper/intro-skipper/total?label=github%20downloads"/>
</a>
</div>

## Manifest URL (All Jellyfin Versions)

```
https://manifest.intro-skipper.org/manifest.json
```

## System requirements

* Jellyfin 10.10.0 (or newer)
* Jellyfin's [fork](https://github.com/jellyfin/jellyfin-ffmpeg) of `ffmpeg` must be installed, version `6.0.1-5` or newer
  * `jellyfin/jellyfin` 10.9.z container: preinstalled
  * `linuxserver/jellyfin` 10.9.z container: preinstalled
  * Debian Linux based native installs: provided by the `jellyfin-ffmpeg6` package
  * MacOS native installs: build ffmpeg with chromaprint support ([instructions](https://github.com/intro-skipper/intro-skipper/wiki/Custom-FFMPEG-(MacOS)))

## Limitations

* SyncPlay is not (yet) compatible with any method of skipping due to the nature of how the clients are synced. 

## Detection parameters

Show introductions will be detected if they are:

* Located within the first 25% of an episode or the first 10 minutes, whichever is smaller
* Between 15 seconds and 2 minutes long

Ending credits will be detected if they are shorter than 4 minutes.

These parameters can be configured by opening the plugin settings

## [Installation](https://github.com/intro-skipper/intro-skipper/wiki/Installation)
- #### [Install the plugin](https://github.com/intro-skipper/intro-skipper/wiki/Installation#step-1-install-the-plugin)
- #### [Verify the plugin](https://github.com/intro-skipper/intro-skipper/wiki/Installation#step-2-verify-the-plugin)
- #### [Custom FFMPEG (MacOS)](https://github.com/intro-skipper/intro-skipper/wiki/Custom-FFMPEG-(MacOS))

## [Jellyfin Skip Options](https://github.com/intro-skipper/intro-skipper/wiki/Jellyfin-Skip-Options)

## [Troubleshooting](https://github.com/intro-skipper/intro-skipper/wiki/Troubleshooting)
- #### [Scheduled tasks fail instantly](https://github.com/intro-skipper/intro-skipper/wiki/Troubleshooting#scheduled-tasks-fail-instantly)
- #### [Skip button is not visible](https://github.com/intro-skipper/intro-skipper/wiki/Troubleshooting#skip-button-is-not-visible)
- #### [Autoskip is not working](https://github.com/intro-skipper/intro-skipper/wiki/Troubleshooting#autoskip-is-not-working)

## [API Documentation](https://github.com/intro-skipper/intro-skipper/blob/master/docs/api.md)

<br />
<p align="center">
  <a href="https://discord.gg/AYZ7RJ3BuA"><img src="https://invidget.switchblade.xyz/AYZ7RJ3BuA"></a>
</p>
