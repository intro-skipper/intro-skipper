### Introskipper - Docker Mod for Jellyfin

This mod ensures the permissions are set correctly so that the skip button works as intended in Jellyfin, to be installed/updated during container start.

To install, set an environment variable in your Jellyfin Docker arguments:

```yaml
DOCKER_MODS=ghcr.io/jumoog/intro-skipper
```

If you are adding multiple mods, enter them in an array separated by `|`, like this:

```yaml
DOCKER_MODS=ghcr.io/jumoog/intro-skipper|linuxserver/mods:jellyfin-mod2
```
