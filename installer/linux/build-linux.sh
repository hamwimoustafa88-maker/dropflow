#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Builds DropFlow for Linux (x64 and ARM64) on a Linux machine with the .NET 10 SDK:
#   build/linux/packages/DropFlow-linux-amd64.deb       DropFlow-linux-arm64.deb        (Debian, Ubuntu, Mint, Pop!_OS: system FFmpeg)
#   build/linux/packages/DropFlow-linux-x86_64.rpm      DropFlow-linux-aarch64.rpm      (Fedora, openSUSE: system FFmpeg)
#   build/linux/packages/DropFlow-linux-x86_64.AppImage DropFlow-linux-aarch64.AppImage (any distribution, FFmpeg inside)
# Asset names carry no version, so "releases/latest/download/<name>" links keep working.
#
#   installer/linux/build-linux.sh VERSION [linux-x64|linux-arm64 ...]
#
# Needs: curl, unzip, squashfs-tools (mksquashfs). nfpm and the AppImage runtime are downloaded at pinned versions and SHA-256 checked.
set -euo pipefail
cd "$(dirname "$0")/../.."

version=${1:?version, e.g. 0.2.0}
shift || true
rids=("$@")
[[ ${#rids[@]} -gt 0 ]] || rids=(linux-x64 linux-arm64)

export AVALONIA_TELEMETRY_OPTOUT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
out=build/linux
packages=$out/packages
tools=$out/tools
mkdir -p "$packages" "$tools"

sha256() { sha256sum "$1" | cut -d' ' -f1; }
fetch() { # url file sha256
  if [[ ! -f "$2" || "$(sha256 "$2")" != "$3" ]]; then curl -fsSL --retry 3 --proto '=https' -o "$2" "$1"; fi
  [[ "$(sha256 "$2")" == "$3" ]] || { echo "SHA-256 mismatch for $1" >&2; exit 1; }
}

# nfpm 2.47.0 (MIT) for the build machine's architecture
case "$(uname -m)" in
  x86_64) fetch https://github.com/goreleaser/nfpm/releases/download/v2.47.0/nfpm_2.47.0_Linux_x86_64.tar.gz "$tools/nfpm.tgz" 0660ca602b2d2d2ae4781a06c692b3eeb9d437ffea05b831d76e41f4a3188783 ;;
  aarch64) fetch https://github.com/goreleaser/nfpm/releases/download/v2.47.0/nfpm_2.47.0_Linux_arm64.tar.gz "$tools/nfpm.tgz" 1c0f5f2999b9a974bfb04fdb0cc3306096de530ac5dbb25d739cc5f5219c919c ;;
  *) echo "unsupported build machine $(uname -m)" >&2; exit 2 ;;
esac
tar -xzf "$tools/nfpm.tgz" -C "$tools" nfpm
nfpm=$tools/nfpm

for rid in "${rids[@]}"; do
  case $rid in
    linux-x64) deb=amd64; long=x86_64; ffarch=amd64
      runtime_sha=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d ;;
    linux-arm64) deb=arm64; long=aarch64; ffarch=arm64
      runtime_sha=00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444 ;;
    *) echo "unsupported runtime $rid" >&2; exit 2 ;;
  esac

  echo "::group::publish $rid"
  publish=$out/publish-$rid
  rm -rf "$publish"
  dotnet publish src/DropFlow.Desktop/DropFlow.Desktop.csproj -c Release -r "$rid" --self-contained true \
    -p:Version="$version" -p:DebugType=none -p:DebugSymbols=false -o "$publish" -nologo
  echo "::endgroup::"

  # The staged tree shared by the packages and the AppImage
  root=$out/root-$rid
  rm -rf "$root"
  mkdir -p "$root/usr/lib/dropflow" "$root/usr/share/applications" "$root/usr/share/icons/hicolor/512x512/apps" \
           "$root/usr/share/icons/hicolor/scalable/apps" "$root/usr/share/gnome-shell/extensions" "$root/usr/share/doc/dropflow"
  cp -R "$publish/." "$root/usr/lib/dropflow/"
  cp installer/linux/dev.scouthub.dropflow.desktop "$root/usr/share/applications/"
  cp src/DropFlow.Desktop/Assets/dropflow-512.png "$root/usr/share/icons/hicolor/512x512/apps/dropflow.png"
  cp src/DropFlow.Desktop/Assets/dropflow.svg "$root/usr/share/icons/hicolor/scalable/apps/dropflow.svg"
  cp -R linux/gnome-extension/dropflow@scouthub.dev "$root/usr/share/gnome-shell/extensions/"
  cp LICENSE "$root/usr/share/doc/dropflow/copyright"
  cp THIRD-PARTY-NOTICES.md TRADEMARK.md "$root/usr/share/doc/dropflow/"
  # Normalise modes (a mounted or Windows-checked-out source tree makes every file executable): data 0644, folders 0755, only the app 0755
  find "$root" -type f -exec chmod 0644 {} +
  find "$root" -type d -exec chmod 0755 {} +
  chmod 0755 "$root/usr/lib/dropflow/dropflow-desktop"

  echo "::group::deb + rpm $rid"
  # nfpm does not expand variables in "contents", so the placeholders are filled in here
  config=$out/nfpm-$rid.yaml
  sed -e "s|\${DROPFLOW_ARCH}|$deb|g" -e "s|\${DROPFLOW_VERSION}|$version|g" -e "s|\${DROPFLOW_ROOT}|$root|g" \
    installer/linux/nfpm.yaml > "$config"
  "$nfpm" package --config "$config" --packager deb --target "$packages/DropFlow-linux-$deb.deb"
  "$nfpm" package --config "$config" --packager rpm --target "$packages/DropFlow-linux-$long.rpm"
  echo "::endgroup::"

  echo "::group::AppImage $rid"
  appdir=$out/AppDir-$rid
  rm -rf "$appdir"
  cp -R "$root" "$appdir"
  rm -rf "$appdir/usr/share/gnome-shell"          # the app copies the extension to the user's folder itself
  installer/unix/fetch-ffmpeg.sh linux "$ffarch" "$appdir/usr/lib/dropflow/engines"
  cp LICENSE "$appdir/usr/lib/dropflow/engines/LICENSE-GPL-3.0.txt"
  cp installer/linux/AppRun "$appdir/AppRun"
  chmod 0755 "$appdir/AppRun"
  cp installer/linux/dev.scouthub.dropflow.desktop "$appdir/dev.scouthub.dropflow.desktop"
  cp src/DropFlow.Desktop/Assets/dropflow-512.png "$appdir/dropflow.png"
  ln -sf dropflow.png "$appdir/.DirIcon"
  fetch "https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-$long" "$tools/runtime-$long" "$runtime_sha"
  rm -f "$out/AppDir-$rid.squashfs"
  mksquashfs "$appdir" "$out/AppDir-$rid.squashfs" -root-owned -noappend -comp zstd -quiet
  cat "$tools/runtime-$long" "$out/AppDir-$rid.squashfs" > "$packages/DropFlow-linux-$long.AppImage"
  chmod 0755 "$packages/DropFlow-linux-$long.AppImage"
  echo "::endgroup::"
done

(cd "$packages" && sha256sum DropFlow-linux-* | tee SHA256SUMS-linux.txt)
