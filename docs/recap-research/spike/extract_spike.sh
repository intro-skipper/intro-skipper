#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
# SPDX-License-Identifier: GPL-3.0-only
#
# End-to-end de-risking spike for subtitle-driven recap detection (RFC A).
#
# Proves, with real ffmpeg/ffprobe, the extraction path the plugin would use:
#   1. mux a synthetic clip with an embedded TEXT subtitle stream ("Previously on…")
#      and a fade-to-black at a known time (the recap boundary);
#   2. enumerate subtitle streams + codec + language with ffprobe (-show_streams);
#   3. extract ONLY the opening window as SubRip to stdout (cheap, text-only);
#   4. detect the black frame with the SAME filter the plugin already uses.
#
# Run: bash docs/recap-research/spike/extract_spike.sh
# Requires ffmpeg + ffprobe on PATH. Writes to a temp dir and cleans up.
set -euo pipefail

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cd "$WORK"

echo "==> work dir: $WORK"

# (0) A recap subtitle: a dense "Previously on…" cluster, a gap, then a normal cue.
cat > recap.srt <<'EOF'
1
00:00:02,000 --> 00:00:05,000
Previously on Test Show...

2
00:00:05,500 --> 00:00:09,000
...the hero lost everything.

3
00:00:30,000 --> 00:00:33,000
Welcome back. Let's begin.
EOF

# (1a) 40s tiny clip; full-black frames in [10,11] act as the recap fade-out.
#      Commas inside the enable() expression are backslash-escaped so no shell
#      quoting is needed (this is exactly how the args are passed to ffmpeg from C#).
ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc=size=320x240:rate=5:duration=40 \
  -vf "drawbox=x=0:y=0:w=in_w:h=in_h:color=black:t=fill:enable=between(t\,10\,11)" \
  -pix_fmt yuv420p video.mp4

# (1b) Mux the subtitle as a TEXT stream into MKV (subrip) and MP4 (mov_text).
ffmpeg -hide_banner -loglevel error -i video.mp4 -i recap.srt \
  -c:v copy -c:s srt -metadata:s:s:0 language=eng episode.mkv
ffmpeg -hide_banner -loglevel error -i video.mp4 -i recap.srt \
  -c:v copy -c:s mov_text -metadata:s:s:0 language=eng episode.mp4

echo
echo "==> (2) ffprobe: enumerate subtitle streams (index, codec, language)"
ffprobe -v error -select_streams s \
  -show_entries stream=index,codec_name,codec_type,disposition:stream_tags=language \
  -of json episode.mkv

echo
echo "==> (3) extract ONLY the opening 15s as SubRip to stdout (mkv/subrip)"
ffmpeg -hide_banner -loglevel error -i episode.mkv -to 15 -map 0:s:0 -f srt -

echo "==> (3b) same for mp4/mov_text"
ffmpeg -hide_banner -loglevel error -i episode.mp4 -to 15 -map 0:s:0 -f srt -

echo
echo "==> (4) black-frame detection in [0,20] (plugin filter: blackframe=amount=50:threshold=28)"
ffmpeg -hide_banner -loglevel info -ss 0 -i episode.mkv -to 20 -an -dn -sn \
  -vf blackframe=amount=50:threshold=28 -f null - 2>&1 | grep -i "blackframe" | head -4

echo
echo "==> spike OK"
