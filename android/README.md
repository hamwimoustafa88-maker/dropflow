# DropFlow for Android

Native Android app (Kotlin, Jetpack Compose, Material 3). Share files to DropFlow from any app and a radial wheel
appears over it: convert, compress, trim, merge, zip and more, 100% on the phone. The app has **no internet
permission**. Architecture and what is verified: [`docs/android.md`](../docs/android.md).

- App id `dev.scouthub.dropflow` (debug builds: `dev.scouthub.dropflow.debug`), minSdk 29 (Android 10), target/compile SDK 36.
- Licence: GPL-3.0-or-later, like the rest of DropFlow. The DropFlow name and logo are reserved (`TRADEMARK.md`).

## Requirements

- JDK 17 or newer (21 is used here and in CI).
- Android SDK with `platforms;android-36` and `build-tools;36.0.0`. Point Gradle at it with `ANDROID_HOME`
  (or `sdk.dir` in `android/local.properties`, which is git-ignored).
- No global Gradle: the wrapper (`gradlew`, `gradlew.bat`, Gradle 9.8.2) downloads it.

## Build and test

```bash
cd android
./gradlew testDebugUnitTest          # JVM unit tests (wheel geometry, catalog mapping, zip-slip guard, naming, ...)
./gradlew assembleDebug              # app/build/outputs/apk/debug/app-debug.apk
./gradlew assembleRelease            # app/build/outputs/apk/release/app-release.apk (signed) or app-release-unsigned.apk
```

On Windows use `gradlew.bat`. The tool list is generated at build time from `catalog/tools.json` (task
`generateCatalog`, output `app/build/generated/source/catalog`); run `node catalog/build.mjs` after editing the catalog
as usual, then rebuild.

Version: `-Pdropflow.versionName=1.2.3` (CI passes the release tag). `versionCode = major*10000 + minor*100 + patch`.

## Release signing

The keystore and its passwords are **never** committed (`*.jks` and `*.keystore` are git-ignored). Gradle reads them from
Gradle properties or environment variables:

| Gradle property (e.g. in `~/.gradle/gradle.properties`) | Environment variable |
|---|---|
| `dropflow.storeFile` | `DROPFLOW_KEYSTORE` |
| `dropflow.storePassword` | `DROPFLOW_KEYSTORE_PASSWORD` |
| `dropflow.keyAlias` | `DROPFLOW_KEY_ALIAS` |
| `dropflow.keyPassword` | `DROPFLOW_KEY_PASSWORD` |

Without them `assembleRelease` produces an unsigned APK. `-Pdropflow.requireSigning=true` turns a missing keystore into
a build failure (CI uses it).

Example (PowerShell):

```powershell
$env:DROPFLOW_KEYSTORE = "$env:USERPROFILE\.dropflow\dropflow-release.jks"
$env:DROPFLOW_KEYSTORE_PASSWORD = "..."   # from the passwords file next to the keystore
$env:DROPFLOW_KEY_ALIAS = "dropflow"
$env:DROPFLOW_KEY_PASSWORD = "..."
.\gradlew.bat assembleRelease
```

Check a signed APK: `"$ANDROID_HOME/build-tools/36.0.0/apksigner" verify --print-certs app/build/outputs/apk/release/app-release.apk`.

Keep a backup of the keystore: if it is lost, new versions can no longer be installed over existing installs.

## CI

`.github/workflows/build-android.yml` is a reusable workflow (`workflow_call` with input `version`, or run by hand). It
runs the unit tests, builds debug and release, then builds the signed release from the secrets `ANDROID_KEYSTORE_BASE64`,
`ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` and `ANDROID_KEY_PASSWORD`, verifies the signature and uploads the
artifact `DropFlow-android.apk`. If the keystore secret is missing it fails on purpose instead of publishing an unsigned APK.

Base64 for the secret (PowerShell): `[Convert]::ToBase64String([IO.File]::ReadAllBytes("$env:USERPROFILE\.dropflow\dropflow-release.jks"))`.

## Install and try on a device

```bash
adb install -r app/build/outputs/apk/debug/app-debug.apk
adb push photo.jpg /sdcard/Download/
# Share it to DropFlow from the Files app, or open DropFlow and press "Choose files".
```
