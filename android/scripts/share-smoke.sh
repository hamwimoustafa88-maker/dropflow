#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Drives DropFlow's real share flow on a running emulator/device: pushes sample files (made by the instrumented
# tests, or any files you give), shares each one to WheelActivity as another app would (content:// from MediaStore),
# and saves screenshots of the wheel in English and Arabic.
#
#   android/scripts/share-smoke.sh OUT_DIR FILE [FILE...]
#
# Needs: adb on PATH (or $ANDROID_HOME/platform-tools), the debug APK installed (dev.scouthub.dropflow.debug).
#
# Note: `adb shell am start --grant-read-uri-permission` cannot grant a MediaStore file that adb pushed (the shell does
# not own it), so DropFlow gets a SecurityException ("The shared file could not be read"). Use the samples the
# instrumented tests export (owned by DropFlow) instead:
#   gradlew connectedDebugAndroidTest -Pandroid.injected.androidTest.leaveApksInstalledAfterRun=true #     -Pandroid.testInstrumentationRunnerArguments.class=dev.scouthub.dropflow.matrix.ExportSamplesTest #     -Pandroid.testInstrumentationRunnerArguments.exportSamples=true
#   ON_DEVICE=Download/DropFlow/samples/ android/scripts/share-smoke.sh OUT "camera photo.jpg" "book.pdf" ...
set -euo pipefail
export MSYS_NO_PATHCONV=1 # Git Bash on Windows: keep /sdcard paths as they are
OUT=${1:?output folder}; shift
ADB=${ADB:-adb}
PKG=dev.scouthub.dropflow.debug
mkdir -p "$OUT"

shot() { "$ADB" exec-out screencap -p > "$OUT/$1.png"; echo "screenshot $OUT/$1.png"; }

share() { # file mime label
  local f=$1 mime=$2 label=$3 name id uri
  name=$(basename "$f")
  local rel=${ON_DEVICE:-Download/}
  if [ -z "${ON_DEVICE:-}" ]; then "$ADB" push "$f" "/sdcard/Download/$name" >/dev/null; sleep 1; fi
  # One string for the device shell, so the quotes of the WHERE clause survive.
  id=$("$ADB" shell "content query --uri content://media/external/file --projection _id --where \"_display_name='$name' AND relative_path='$rel'\"" \
        | sed -n 's/.*_id=\([0-9]*\).*/\1/p' | tail -1)
  [ -n "$id" ] || { echo "MediaStore has no row for $name"; return 1; }
  uri="content://media/external/file/$id"
  "$ADB" shell am force-stop $PKG
  "$ADB" shell am start -W -a android.intent.action.SEND -t "$mime" --grant-read-uri-permission \
     --eu android.intent.extra.STREAM "$uri" -n $PKG/dev.scouthub.dropflow.WheelActivity >/dev/null
  sleep 3
  shot "$label"
}

for lang in en ar; do
  "$ADB" shell cmd locale set-app-locales $PKG --locales "$lang" >/dev/null || true
  for f in "$@"; do
    ext=${f##*.}
    case "$ext" in
      jpg|jpeg) mime=image/jpeg ;; png) mime=image/png ;; heic) mime=image/heic ;; webp) mime=image/webp ;;
      pdf) mime=application/pdf ;; mp4) mime=video/mp4 ;; webm) mime=video/webm ;; m4a) mime=audio/mp4 ;;
      wav) mime=audio/x-wav ;; ogg) mime=audio/ogg ;; zip) mime=application/zip ;; *) mime=application/octet-stream ;;
    esac
    share "$f" "$mime" "wheel-$lang-$(basename "$f" | tr ' ' '_')"
  done
done
"$ADB" shell cmd locale set-app-locales $PKG --locales "" >/dev/null || true
