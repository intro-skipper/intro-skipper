# Intro Skipper (beta)

<div align="center">
<img alt="Plugin Banner" src="https://raw.githubusercontent.com/jumoog/intro-skipper/master/images/logo.png" />
</div>

Analyzes the audio of television episodes to detect and skip over intros.

This fork doesn't ship the custom web interface on your server. So the plugin needs to be configured to automatically skip intros.

## System requirements

* Jellyfin 10.8.4 (or newer)
* Jellyfin's [fork](https://github.com/jellyfin/jellyfin-ffmpeg) of `ffmpeg` must be installed, version `5.0.1-5` or newer
  * `jellyfin/jellyfin` 10.8.z container: preinstalled
  * `linuxserver/jellyfin` 10.8.z container: preinstalled
  * Debian Linux based native installs: provided by the `jellyfin-ffmpeg5` package
  * MacOS native installs: build ffmpeg with chromaprint support ([instructions](#installation-instructions-for-macos))

## Introduction requirements

Show introductions will only be detected if they are:

* Located within the first 25% of an episode, or the first 10 minutes, whichever is smaller
* Between 15 seconds and 2 minutes long

Ending credits will only be detected if they are shorter than 4 minutes.

All of these requirements can be customized as needed.

## Installation instructions

### Step 1: Install the plugin
1. Add this plugin repository to your server: `https://raw.githubusercontent.com/jumoog/intro-skipper/master/manifest.json`
2. Install the Intro Skipper plugin from the General section
3. Restart Jellyfin
4. If you did not install the modified web interface, enable automatic skipping
    1. Go to Dashboard -> Plugins -> Intro Skipper
    2. Check "Automatically skip intros" and click Save
5. Go to Dashboard -> Scheduled Tasks -> Analyze Episodes and click the play button
6. After a season has completed analyzing, play some episodes from it and observe the results
    1. Status updates are logged before analyzing each season of a show

## Installation instructions for MacOS

1. Build ffmpeg with chromaprint support using brew:

```
brew uninstall --force --ignore-dependencies ffmpeg
brew install chromaprint amiaopensource/amiaos/decklinksdk
brew tap homebrew-ffmpeg/ffmpeg
brew install homebrew-ffmpeg/ffmpeg/ffmpeg --with-chromaprint
brew link --overwrite ffmpeg
```

2. Retrieve ffmpeg path with `whereis ffmpeg` and use this path on Jellyfin under [encoding settings](http://localhost:8096/web/index.html#!/encodingsettings.html)

3. Follow the [installation instructions](#installation-instructions) above

## Documentation

Documentation about how the API works can be found in [api.md](docs/api.md).
