#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Builds DropFlow.app and its disk image on a Mac with the .NET 10 SDK and Xcode's command-line tools:
#   build/macos/packages/DropFlow-mac-arm64.dmg   (Apple Silicon)
#   build/macos/packages/DropFlow-mac-x64.dmg     (Intel)
#
#   installer/macos/build-macos.sh VERSION [arm64|x64 ...]
#
# The app is ad-hoc signed only (codesign --sign -): the owner decided against the paid Apple Developer Program (docs/macos.md), so
# there is no notarization. macOS therefore asks once on first launch; the DMG carries "Read me first" with the right-click → Open /
# "Open Anyway" steps, and the app's welcome hint repeats them. FFmpeg + ffprobe 9.0.2 (GPL-3.0, pinned + SHA-256 checked) ship in
# Contents/Resources/engines. qpdf and Tesseract have no pinned, redistributable macOS build, so the app shows "brew install …" for them.
set -euo pipefail
cd "$(dirname "$0")/../.."

version=${1:?version, e.g. 0.2.0}
shift || true
arches=("$@")
[[ ${#arches[@]} -gt 0 ]] || arches=(arm64 x64)

export AVALONIA_TELEMETRY_OPTOUT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
out=build/macos
mkdir -p "$out/packages"

# The icon: one 1024-capable PNG → every size Finder asks for → .icns
iconset=$out/dropflow.iconset
rm -rf "$iconset" && mkdir -p "$iconset"
for s in 16 32 128 256 512; do
  sips -z $s $s src/DropFlow.Desktop/Assets/dropflow-512.png --out "$iconset/icon_${s}x${s}.png" >/dev/null
  d=$((s * 2)); [[ $d -le 512 ]] || d=512
  sips -z $d $d src/DropFlow.Desktop/Assets/dropflow-512.png --out "$iconset/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$out/dropflow.icns"

for arch in "${arches[@]}"; do
  case $arch in
    arm64) rid=osx-arm64; ffarch=arm64 ;;
    x64) rid=osx-x64; ffarch=amd64 ;;
    *) echo "unsupported arch $arch" >&2; exit 2 ;;
  esac

  echo "::group::publish $rid"
  publish=$out/publish-$rid
  rm -rf "$publish"
  dotnet publish src/DropFlow.Desktop/DropFlow.Desktop.csproj -c Release -r "$rid" --self-contained true \
    -p:Version="$version" -p:DebugType=none -p:DebugSymbols=false -o "$publish" -nologo
  echo "::endgroup::"

  app=$out/$arch/DropFlow.app
  rm -rf "$out/$arch" && mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  cp -R "$publish/." "$app/Contents/MacOS/"
  rm -rf "$app/Contents/MacOS/gnome-extension"              # Linux only
  sed "s/__VERSION__/$version/g" installer/macos/Info.plist > "$app/Contents/Info.plist"
  plutil -lint "$app/Contents/Info.plist"
  cp -R installer/macos/en.lproj installer/macos/ar.lproj "$app/Contents/Resources/"
  cp "$out/dropflow.icns" "$app/Contents/Resources/dropflow.icns"
  cp LICENSE THIRD-PARTY-NOTICES.md TRADEMARK.md "$app/Contents/Resources/"

  echo "::group::FFmpeg $arch"
  installer/unix/fetch-ffmpeg.sh macos "$ffarch" "$app/Contents/Resources/engines"
  cp LICENSE "$app/Contents/Resources/engines/LICENSE-GPL-3.0.txt"
  echo "::endgroup::"

  echo "::group::sign $arch (ad-hoc)"
  chmod 0755 "$app/Contents/MacOS/dropflow-desktop"
  xattr -cr "$app"
  codesign --force --deep --sign - --identifier dev.scouthub.dropflow "$app"
  codesign --verify --deep --strict --verbose=2 "$app"
  echo "::endgroup::"

  echo "::group::dmg $arch"
  staging=$out/dmg-$arch
  rm -rf "$staging" && mkdir -p "$staging"
  cp -R "$app" "$staging/"
  ln -s /Applications "$staging/Applications"
  cp installer/macos/README-first.txt "$staging/Read me first.txt"
  dmg=$out/packages/DropFlow-mac-$arch.dmg
  rm -f "$dmg"
  hdiutil create -volname "DropFlow" -srcfolder "$staging" -fs HFS+ -format UDZO -ov "$dmg"
  echo "::endgroup::"
done

(cd "$out/packages" && shasum -a 256 DropFlow-mac-*.dmg | tee SHA256SUMS-macos.txt)
