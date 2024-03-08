if [ "$(uname)" == "Darwin" ]; then
  # MacOS
  if [ -d ~/.local/share/jellyfin/plugins/ ]; then
    plugin=$(ls -d ~/.local/share/jellyfin/plugins/Intro\ Skipper* | sort -r | head -n 1 )
    if [ -z "$plugin" ]; then
        echo "Intro Skipper plugin not found!"
        exit
    fi
    cp -f ConfusedPolarBear.Plugin.IntroSkipper*.dll \
    "$plugin/ConfusedPolarBear.Plugin.IntroSkipper.dll"
  else
    echo "Jellyfin plugin directory not found!"
  fi
elif [ "$(expr substr $(uname -s) 1 5)" == "Linux" ]; then
  # Linux
  if [ -d /var/lib/jellyfin/plugins/ ]; then
    plugin=$(ls -d /var/lib/jellyfin/plugins/Intro\ Skipper* | sort -r | head -n 1 )
    if [ -z "$plugin' ]; then
        echo "Intro Skipper plugin not found!"
        exit
    fi
    cp -f ConfusedPolarBear.Plugin.IntroSkipper*.dll \
    "$plugin/ConfusedPolarBear.Plugin.IntroSkipper.dll"
  else
    echo "Jellyfin plugin directory not found!"
  fi
fi
