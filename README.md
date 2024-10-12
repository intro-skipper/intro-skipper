# Intro Skipper (beta)
<div align="center">
    <p>
        <img alt="Plugin Banner" src="https://raw.githubusercontent.com/jumoog/intro-skipper/master/images/logo.png" />
    </p>
    <p>
        Analyzes the audio of television episodes to detect and skip over intros.
    </p>
</div>

## Manifest URL for all Jellyfin Versions

```
https://manifest.intro-skipper.workers.dev/
```

## System requirements

* Jellyfin 10.8.4 (or newer)
* Jellyfin's [fork](https://github.com/jellyfin/jellyfin-ffmpeg) of `ffmpeg` must be installed, version `5.0.1-5` or newer
  * `jellyfin/jellyfin` 10.8.z container: preinstalled
  * `linuxserver/jellyfin` 10.8.z container: preinstalled
  * Debian Linux based native installs: provided by the `jellyfin-ffmpeg5` package
  * MacOS native installs: build ffmpeg with chromaprint support ([instructions](https://github.com/jumoog/intro-skipper/wiki/Custom-FFMPEG-(MacOS)))

## Detection parameters

Show introductions will be detected if they are:

* Located within the first 25% of an episode or the first 10 minutes, whichever is smaller
* Between 15 seconds and 2 minutes long

Ending credits will be detected if they are shorter than 4 minutes.

These parameters can be configured by opening the plugin settings

## [Installation](https://github.com/jumoog/intro-skipper/wiki/Installation)

## [Troubleshooting](https://github.com/jumoog/intro-skipper/wiki/Troubleshooting)

## [API Documentation](https://github.com/jumoog/intro-skipper/blob/master/docs/api.md)

<br />
<p align="center">
  <a href="https://discord.gg/AYZ7RJ3BuA"><img src="https://invidget.switchblade.xyz/AYZ7RJ3BuA"></a>
</p>


