# DropFlow for Android: architecture and verification

Native app in `android/` (Kotlin, Jetpack Compose, Material 3), app id `dev.scouthub.dropflow`, minSdk 29 (Android 10),
compile/target SDK 36 (the newest SDK installed on the build PC). Build steps: [`android/README.md`](../android/README.md).

## How it works

```
another app ──share/open──▶ WheelActivity (translucent, over the sending app)
                               │ Inputs.copy: content:// only, copied into cache/inputs/<session>/ (originals never opened for writing)
                               ▼
                           AndroidTools.wheelFor(kinds) ──▶ Convert / Tools / Actions rings (WheelView)
                               │ tap a petal, or slide from the hub and lift
                               ├─ Actions (share, open with, copy to a folder) run in the activity
                               └─ options form / trim editor ──▶ JobCenter.submit ──▶ WorkService (foreground, progress notification)
                                                                                        │ Runner ▶ ops/* in cache/jobs/<id>/
                                                                                        ▼
                                                                    Publisher (MediaStore) ▶ Pictures|Movies|Music|Download/DropFlow
                                                                                        ▼
                                                                    result sheet (Open / Share / Copy text) or "done" notification
```

| Part | File(s) | Notes |
|---|---|---|
| Catalog link | `app/build.gradle.kts` (task `generateCatalog`), `catalog/CatalogModel.kt` | `catalog/tools.json` is turned into `CatalogData.kt` at build time; tool labels (ar/en) come from it. |
| Android tool table | `catalog/AndroidTools.kt` | Every entry carries the catalog id it implements. Entries the catalog has no id for (format targets, `img.rotate`, `act.open-with`) use their own strings. |
| Wheel | `wheel/WheelGeometry.kt` (pure), `ui/WheelView.kt` | Petal 0 at the top, clockwise, **same geometry in Arabic** (the layout runs inside a forced LTR direction); labels use `TextDirection.Content` so Arabic shapes and aligns RTL. Up to 8 petals, then a "More" petal pages through. Haptic tick on each petal crossed, confirm haptic on selection. TalkBack: each label is a button with an onClick action. |
| Inputs | `work/Inputs.kt` | Accepts `content://` URIs only (a `file://` URI or DropFlow's own FileProvider is refused, so another app cannot make DropFlow copy its private files out). Names are sanitised. |
| Jobs | `work/Jobs.kt`, `work/WorkService.kt`, `work/Notifications.kt` | One job at a time, queue in a `LifecycleService`; foreground type `mediaProcessing` on Android 15+, `dataSync` before. Cancel from the sheet or the notification. |
| Output | `work/Publisher.kt`, `core/OutputNaming.kt` | MediaStore inserts (no storage permission), `IS_PENDING` while writing. Names like the Windows app: `name (converted).ext`, then `name (converted 2).ext`; many-file results go into a sub-folder (`report (pages)`, `photos (extracted)`). Archive contents all go to Download/DropFlow to stay together. |
| Images | `ops/ImageOps.kt`, `ops/Qr.kt` | ImageDecoder (applies EXIF orientation, caps at ~36 MP), Bitmap.compress for JPG/PNG/WebP, HeifWriter for HEIC (offered only if an HEVC encoder exists), PdfDocument for images to PDF, ZXing for QR. Target-size compression = binary search over quality (`core/TargetSize.kt`), then 0.75× downscale steps if even quality 5 is too big. |
| PDF | `ops/PdfOps.kt` | PdfRenderer for pages to PNG/JPG; PdfBox-Android for merge, split, rotate, metadata removal, image recompression and text. Protected PDFs are refused (never cracked or unlocked). |
| Video/audio | `ops/MediaOps.kt`, `core/MediaPlans.kt` | Media3 Transformer (hardware H.264/HEVC/AAC), MediaExtractor + MediaMuxer for lossless stream copies when the codecs already fit MP4/M4A, MediaCodec for WAV, MediaMetadataRetriever for frames. Trim/frame editor with an ExoPlayer preview (`ui/Screens.kt`). |
| Archives | `ops/ArchiveOps.kt`, `core/ZipGuard.kt` | java.util.zip. Every entry is resolved through `ZipGuard` before anything is written; one bad entry refuses the whole archive. Limits: 20,000 entries, 8 GB expanded. Name encodings tried: UTF-8, then CP437. |
| Language | `Language.kt`, `res/values{,-ar}/strings.xml`, `res/xml/locales_config.xml` | Follows the system; in-app switch (System / English / العربية). Android 13+: per-app language (also in system settings); older: applied to each activity/service context. |
| Brand | `ui/Theme.kt`, `res/values/colors.xml`, `res/drawable/ic_launcher_*.xml` | Colours from `web/src/app/globals.css`; adaptive launcher icon (and themed monochrome layer, and the notification icon) drawn from `web/src/app/icon.svg` paths. |

Privacy: the merged manifest has **no INTERNET or network-state permission** (both removed explicitly with
`tools:node="remove"`; checked with `aapt2 dump permissions` on the release APK). Backups and device transfer are
excluded (`data_extraction_rules.xml`, `allowBackup=false`).

## Tool coverage (Android vs catalog)

| Ring | Entries | Catalog ids |
|---|---|---|
| Convert | image → JPG, PNG, WebP, HEIC*, PDF · PDF → PNG, JPG, TXT · video → MP4 (H.264), MP4 (HEVC)\*\*, M4A, WAV · audio → M4A, WAV | targets are checked against the catalog category outputs by a test |
| Tools | `img.compress`, `img.resize`, `img.rotate` (Android only), `img.strip-gps`, `img.to-pdf`, `img.qr` (read; create from shared text), `pdf.compress`, `pdf.merge`, `pdf.split`, `pdf.rotate`, `pdf.metadata` (removes it), `pdf.to-images`, `pdf.extract-text`, `vid.compress`, `vid.trim`, `vid.mute`, `vid.snapshot`, `aud.trim`, `arc.extract`, `arc.zip` | |
| Actions | `act.share`, `act.open-with` (Android only), `act.copy` (copy to a folder via the system picker) | |

\* only when the phone has an HEVC encoder. \*\* only with an HEVC video encoder, and refused (not shrunk) when that encoder cannot take the video's size. Not done on Android (yet): audio normalize (optional in the brief), the
catalog's other tools (OCR, background removal, redaction, metadata editing, watermarks, protect/unlock, ...).

## What is verified

### On the Android 15 emulator (2026-11-12)

AVD `dropflow_api35` (system image `android-35;google_apis;x86_64`, Pixel 6 profile, WHPX acceleration) on the build PC.
Instrumented tool matrix in `android/app/src/androidTest/java/dev/scouthub/dropflow/matrix/` (test-only dependencies
AndroidX Test runner/rules/ext-junit/uiautomator, Apache-2.0, never in the app). It makes real sample files on the device
(nothing downloaded), runs every wheel entry through the same `Runner` the app uses, and checks the output (images
decoded with size/format/orientation/alpha/EXIF/XMP; PDFs re-opened for page count, rotation, metadata and text in
reading order; media with MediaExtractor/MediaMetadataRetriever for codec, tracks, duration and shown size; WAV header;
ZIP entries; MediaStore folder, name counting, `IS_PENDING` and bytes). Each (sample, tool) row is logged with the tag
`DFMATRIX`. Last run: **258 rows, 0 failures**, plus 65 JVM unit tests.

Samples: JPEG with EXIF orientation 6 + GPS + XMP-GPS, JPEG without EXIF, PNG with transparency, WebP, HEIC (HeifWriter),
GIF, BMP, a 39 MP JPEG, a QR PNG with Arabic; English 3-page PDF, Arabic PDF drawn by Android/Skia (like Chrome print, with
an Arabic + English line), Arabic PDF in presentation forms/visual order, scanned (image-only) PDF, 12-page PDF, photo PDF,
AES-protected PDF, a text file named .pdf; MP4 H.264+AAC, MP4 rotated 90°, MP4 HEVC+AAC, MP4 640x480 6 Mbit/s noisy,
MP4 without sound, WebM VP8+Opus; WAV, M4A AAC, OGG Opus, 3GP AMR-NB; ZIP with folders and Arabic names, ZIP with DOS
(CP437) names, zip-slip ZIP, empty ZIP.

Real share flow (`android/scripts/share-smoke.sh`, `adb shell am start -a android.intent.action.SEND` with MediaStore
`content://` URIs): the wheel for a photo, an Arabic PDF, a video and a ZIP in English and Arabic; Arabic PDF → TXT,
JPG → PNG, video → Remove audio and the trim editor (Arabic) were run by tapping the screen, and the results appeared in
Download / Pictures / Movies `/DropFlow`.

Bugs this found and fixed (each with a test that failed first):

| Bug | Fix |
|---|---|
| **Every job crashed the app on Android 15+** (`InvalidForegroundServiceTypeException: Starting FGS with type none`): androidx.core 1.18 `ServiceCompat.startForeground` masks the type to the API 34 set and drops `mediaProcessing` | `Service.startForeground(id, n, type)` directly; `core/ForegroundPlan` |
| PDF → text: Arabic words came out in reverse order ("بالعالم مرحبا"); PdfBox-Android applies bidi per word, not per line | `ops/LogicalTextStripper` + `core/BidiWords` reorder each line's words |
| Rotate on a JPEG without an orientation tag did nothing (`ExifInterface.rotate` ignores "undefined") | `core/ExifOrientation` |
| ZIP from old Windows (CP437 names) extracted as `R�sum�.txt`: Android's ZipFile does not fail, it inserts U+FFFD | `ZipGuard.needsLegacyNames` → reopen with CP437 |
| "Compress" could save bigger files (video at the preset bitrate, rewritten PDFs, images) | results not smaller than the original are not kept (`error_already_small`), video bitrate capped by the source's (`MediaPlans.videoBitrateFor`) |
| Target-size compression could save a file above the target | refuses with `error_target_unreachable` (`TargetSize.searchShrinking`) |
| MP4 (HEVC) quietly shrank the video (640x480 → 512x384) when the HEVC encoder cannot take the size, and was offered without an HEVC encoder | refuses with `error_hevc_size`; offered only with an HEVC encoder |
| PDF → text on a scanned PDF saved an empty .txt | `error_pdf_no_text` |
| Remove location left GPS in the XMP block | `core/Xmp`, XMP dropped when it carries a location |
| Inputs named only by a MediaStore id (`1000000034`, `image:34`) had no extension | `OutputNaming.withTypeExtension` |
| Android 14+ refuses `../` zip entries itself; that was reported as "damaged" | `ZipGuard.isPlatformRefusal` → "unsafe" |
| "Open with" did not list DropFlow for `application/x-zip-compressed` | manifest VIEW filter |
| Trim/frame timeline ran right to left in Arabic | timeline kept left-to-right (media controls are not mirrored) |

### On the build PC (2026-10-22)

- `gradlew.bat testDebugUnitTest assembleDebug assembleRelease lintDebug`: BUILD SUCCESSFUL, 0 compiler warnings in
  app code, lint 0 errors.
- **46 JVM unit tests pass, 0 fail**: wheel geometry and hit testing (petal centres, boundaries, wrap-around, hub, gap,
  slide past the edge, one petal, paging), catalog mapping (generated table matches `catalog/tools.json`, every catalog
  id exists, convert targets are catalog outputs, multi-file rules, wheels for image / PDF / video / mixed / text),
  target-size binary search, zip-slip guard (14 hostile names, a real archive with `../../` — nothing written outside),
  extraction budget, output naming (`name (converted).ext`, collision counting, sanitising), page ranges (with Arabic
  digits and comma), remux decisions, scaled video sizes, WAV header and float→16-bit, file-kind detection,
  ar/en string parity, and that every error key raised in code has a message.
- Release APK signed with the release key (APK Signature Scheme v2, verified with `apksigner verify`), R8-shrunk.
- Merged manifest permissions: POST_NOTIFICATIONS, FOREGROUND_SERVICE (+ DATA_SYNC, MEDIA_PROCESSING), WAKE_LOCK (from
  a library) — no network permission.

## What is NOT verified

Only the Android 15 x86_64 emulator was used (no phone). Still open:

- Real phones and other Android versions (10-14, 16): hardware codecs (the emulator uses Google's software encoders,
  whose HEVC encoder stops at 512 px and whose H.264 encoder overshoots low bitrates), OEM MediaStore and file managers,
  performance and memory on large real files (only a 39 MP JPEG and short clips were tried), HDR/Dolby Vision video,
  MP3 and FLAC input (no encoder on the device to make samples), AVIF (needs Android 12+ decoders).
- Sharing from real apps (Gallery, Files, WhatsApp) with their own URI grants: `adb shell am start
  --grant-read-uri-permission` cannot grant a file adb pushed, so the share flow used MediaStore files owned by DropFlow.
- PDFs from Word, LibreOffice, scanners and phones' "print to PDF" other than Android/Skia; the word-order fix assumes
  PdfBox hands over each word in logical order, which held for both Arabic samples. Arabic in PDFs with Type 3 fonts or
  without a ToUnicode map is not extractable by any tool.
- Haptics, slide-and-lift, the More page, notifications and the 13+ permission prompt, Open/Share buttons, copy to a
  folder, the language switch on Android 10-12. The trim editor preview was seen; playback was not.
- The CI workflow `.github/workflows/build-android.yml` does not run the instrumented tests (no emulator in CI).

Rerun:

```bash
cd android
./gradlew testDebugUnitTest lintDebug assembleDebug          # JVM tests (65), lint
emulator -avd dropflow_api35 -no-window -no-audio -no-snapshot &   # any API 29+ emulator or a phone
./gradlew connectedDebugAndroidTest                          # tool matrix; rows: adb logcat -d -s DFMATRIX:I
# share flow and screenshots (samples exported by the tests, owned by DropFlow):
./gradlew connectedDebugAndroidTest -Pandroid.injected.androidTest.leaveApksInstalledAfterRun=true   -Pandroid.testInstrumentationRunnerArguments.class=dev.scouthub.dropflow.matrix.ExportSamplesTest   -Pandroid.testInstrumentationRunnerArguments.exportSamples=true
ON_DEVICE=Download/DropFlow/samples/ scripts/share-smoke.sh /tmp/shots "camera photo.jpg" "book.pdf" "phone clip.mp4"
```

On Windows the AVD manager may write Arabic-Indic digits into `config.ini` (`disk.dataPartition.size=٢G`) when the
system format is Arabic; replace them with ASCII digits before starting the emulator.
