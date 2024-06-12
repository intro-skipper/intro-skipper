# Opencl-Intel - Docker mod for Jellyfin

This mod adds skip button to jellyfin, to be installed/updated during container start.

In jellyfin docker arguments, set an environment variable `DOCKER_MODS=ghcr.io/jumoog/intro-skipper`

If adding multiple mods, enter them in an array separated by `|`, such as `DOCKER_MODS=ghcr.io/jumoog/intro-skipper|linuxserver/mods:jellyfin-mod2`
