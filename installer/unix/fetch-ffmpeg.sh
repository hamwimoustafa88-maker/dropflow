#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Downloads the FFmpeg + ffprobe that ship inside the macOS app and the Linux AppImage, and checks them against pinned SHA-256 sums.
#
#   installer/unix/fetch-ffmpeg.sh macos|linux arm64|amd64 OUTDIR
#
# Source: Martin Riedl's static FFmpeg 9.0.2 release builds (https://ffmpeg.martin-riedl.de, build script
# https://git.martin-riedl.de/ffmpeg/build-script). Same FFmpeg version as the Windows app (Gyan 9.0.2). The builds are configured with
# --enable-gpl --enable-version3 and no --enable-nonfree, so the binaries are GPL-3.0; DropFlow ships them unmodified as separate
# programs (never linked) with the GPL text and a source offer, exactly like the Windows installer (ADR-0012).
# The URLs carry the build's timestamp and version, so they do not change; a new pin means new URLs and new sums here.
# Nothing is executed from the download: the archives are checked, then only the two binaries are copied out.
set -euo pipefail

os=${1:?macos or linux}
arch=${2:?arm64 or amd64}
out=${3:?output folder}
base=https://ffmpeg.martin-riedl.de/download

case "$os/$arch" in
  macos/arm64) dir=macos/arm64/1789931890_9.0.2
    ffmpeg_sha=c8ed4c4e6978a03c485edbfe4e0a5dc2380f8a30bba5150531b31b094492d924
    ffprobe_sha=fcbe839537485eaee7a7a8bc5cbc0f90d53617e80943e8a5b2e31cb851197ea6 ;;
  macos/amd64) dir=macos/amd64/1789931006_9.0.2
    ffmpeg_sha=7c6b4125b191cbf773832dc51f424cf2b6bb7da43007d1e066f95909e47cacd4
    ffprobe_sha=2322438ed2f6319a691291b247d09c69dcaa3a982460d1f269a7e1af335cfdfd ;;
  linux/amd64) dir=linux/amd64/1789931100_9.0.2
    ffmpeg_sha=fa8ecf4abbd290d98f7d188b8649cc6b391ae209a98452be955a15aab1909d7f
    ffprobe_sha=3f428c49070be3d24ec338602b76d412e401ffcb8a5641ef0e729181a232fc32 ;;
  linux/arm64) dir=linux/arm64/1789931697_9.0.2
    ffmpeg_sha=93a76ae90db5474eecdf951a729857c64f3de23567228d6a7d5e6e8e3cd1021b
    ffprobe_sha=bcbe80fb741c180083327afaf5434812e006b33cacde2016b9aeaf6936128330 ;;
  *) echo "unsupported: $os/$arch" >&2; exit 2 ;;
esac

sha256() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

mkdir -p "$out"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

for tool in ffmpeg ffprobe; do
  expected_var="${tool}_sha"
  expected=${!expected_var}
  archive="$work/$tool.zip"
  curl -fsSL --retry 3 --proto '=https' -o "$archive" "$base/$dir/$tool.zip"
  actual=$(sha256 "$archive")
  if [[ "$actual" != "$expected" ]]; then
    echo "SHA-256 mismatch for $tool ($os/$arch): expected $expected, got $actual" >&2
    exit 1
  fi
  mkdir -p "$work/$tool"
  unzip -q -o "$archive" -d "$work/$tool"
  binary=$(find "$work/$tool" -type f -name "$tool" | head -n 1)
  [[ -n "$binary" ]] || { echo "$tool not found in its archive" >&2; exit 1; }
  install -m 0755 "$binary" "$out/$tool"
  echo "$tool $os/$arch: sha256 $actual"
done

cat > "$out/FFMPEG-SOURCE-OFFER.txt" <<EOF
DropFlow includes FFmpeg 9.0.2 (ffmpeg and ffprobe) as separate, unmodified programs.
Build: Martin Riedl's static release build, $base/$dir/
Configuration: --enable-gpl --enable-version3 (no non-free parts). Licence: GNU General Public License version 3 (see LICENSE-GPL-3.0.txt).
FFmpeg source code: https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz
Build script and the versions of the libraries built in (x264, x265, libvpx, libaom, dav1d, SVT-AV1, rav1e, lame, opus, vorbis, ...):
https://git.martin-riedl.de/ffmpeg/build-script and $base/$dir/versions.txt
You may also ask the DropFlow project for the complete corresponding source: https://github.com/hamwimoustafa88-maker/dropflow/issues
EOF
