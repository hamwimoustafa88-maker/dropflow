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
| Convert | image → JPG, PNG, WebP, HEIC*, PDF · PDF → PNG, JPG, TXT · video → MP4 (H.264), MP4 (HEVC), M4A, WAV · audio → M4A, WAV | targets are checked against the catalog category outputs by a test |
| Tools | `img.compress`, `img.resize`, `img.rotate` (Android only), `img.strip-gps`, `img.to-pdf`, `img.qr` (read; create from shared text), `pdf.compress`, `pdf.merge`, `pdf.split`, `pdf.rotate`, `pdf.metadata` (removes it), `pdf.to-images`, `pdf.extract-text`, `vid.compress`, `vid.trim`, `vid.mute`, `vid.snapshot`, `aud.trim`, `arc.extract`, `arc.zip` | |
| Actions | `act.share`, `act.open-with` (Android only), `act.copy` (copy to a folder via the system picker) | |

\* only when the phone has an HEVC encoder. Not done on Android (yet): audio normalize (optional in the brief), the
catalog's other tools (OCR, background removal, redaction, metadata editing, watermarks, protect/unlock, ...).

## What is verified

On the build PC (Windows 10, JDK 21, Android SDK platform 36 / build-tools 36.0.0), 2026-10-22:

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

No Android device or emulator was available (`adb devices` empty; no emulator system image installed, and none was
downloaded on purpose). So **nothing has run on Android yet**:

- The wheel has not been seen: layout, colours, Arabic labels in petals, haptics, slide-and-lift, the More page.
- Share-target flow from real apps (Gallery, Files, WhatsApp), copying large inputs, the translucent window.
- Every operation on real files: image conversions (HEIC needs an HEVC encoder), EXIF removal and the lossless JPEG
  rotation, images to PDF, QR read/create, all PDF tools (PdfBox-Android on device, Arabic text extraction order),
  video convert/compress/trim/mute and the stream-copy paths, WAV decoding, frame capture, zip/unzip.
- Foreground service on Android 14/15 (type rules), notifications and the 13+ permission prompt, MediaStore saving and
  the Open/Share buttons, copy to a folder, the language switch on Android 10-12 vs 13+.
- The CI workflow `.github/workflows/build-android.yml` has not run on GitHub yet.

First smoke test once a device is connected:

```bash
adb install -r android/app/build/outputs/apk/debug/app-debug.apk
adb push photo.jpg /sdcard/Download/photo.jpg
adb shell am start -a android.intent.action.SEND -t image/jpeg --grant-read-uri-permission \
  --eu android.intent.extra.STREAM content://media/external/images/media/<id> \
  -n dev.scouthub.dropflow.debug/dev.scouthub.dropflow.WheelActivity
adb exec-out screencap -p > wheel.png
```

(`file://` URIs are refused on purpose, so share a `content://` URI: find the MediaStore id with
`adb shell content query --uri content://media/external/images/media --projection _id:_display_name`.)
