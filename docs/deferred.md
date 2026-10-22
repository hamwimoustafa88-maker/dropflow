# Deferred work — what is postponed, why, and how to resume

Everything that can be built and verified on the development machine has been. What is listed here is **postponed on purpose**:
it needs a decision, an account, a download the owner has not approved, hardware, or a real person at the keyboard.
For what is verified and what is not, see [status.md](status.md). Last updated: 2026-10-23 (translation: C12-C14).

## A. Needs the owner (decision, account or consent)

| # | Item | Why it is waiting | How to resume |
|---|---|---|---|
| A1 | **Phase 0 drag test** (`docs/phase0-checklist.md`, rows 1-17) | **Owner reports it passed (2026-10-21).** Per-row results were not recorded here. | Optional: note any failing rows, and repeat on Windows 11, a second monitor and 125-150 % scaling (B1/B2). The input-capture decision record is still unwritten (ADR-0010 is now the licence ADR, so use the next free number). |
| A2 | **Engines** | **Done:** FFmpeg, qpdf and Tesseract (+Arabic data) ship inside the installer (ADR-0012); users download nothing. | Only LibreOffice stays optional (legacy Office formats). A lawyer should review shipping the GPL FFmpeg as a separate program. |
| A3 | **AI models** and Arabic OCR data | Arabic Tesseract data is bundled and measured (see status.md). Models (Whisper tiny, u2netp, birefnet-lite, ultraface) work on the dev PC; end users still download models after a consent dialog (size). | Open: measure Whisper Arabic quality (needs real Arabic speech + transcript; `ggml-small` / `turbo` not downloaded), cut-out and face-redaction quality on real photos. |
| A4 | **GitHub Actions** | **Done:** the repository is public, so Actions run free; CI is green (catalog, Windows build+tests, website) and `windows-smoke.yml` passed on Windows 10-class and Windows 11 24H2-class images. | Making the repo private again stops free Actions and breaks release downloads and the updater. |
| A5 | **First release** | **Done:** v0.1.0 at https://github.com/hamwimoustafa88-maker/dropflow/releases/tag/v0.1.0 (Setup 263 MB, portable, CLI zip, checksums), built by `release.yml` with the bundled engines. **Unsigned.** | Open: signing (see below), winget submission, installer size. Signing options: Certum cloud OV about $115/yr (any country; check current price), Microsoft Artifact Signing about $10/month (individuals only in USA/Canada), Microsoft Store (MSIX, store signs, small one-time fee; needs MSIX packaging). SignPath free tier is open-source only, so it does not apply. |
| A6 | **Vercel** | **Done:** https://dropflow.scouthub.dev serves the new landing, privacy and terms. | The MCP connector still answers 403 for team `moustafa-hamwis-projects` (its grant lacks that team): reconnect it and tick the team in Vercel's consent screen if you want logs read from here. |
| A7 | **Landing page buttons** | **Done:** "Windows" links to the GitHub release page and "Web" to /app. | Remove `robots: noindex` in `web/src/app/[locale]/layout.tsx` when you want search engines to list the site. |
| A8 | **Accounts, sign-up, payments** (Phase 9) | Deferred by decision (ADR-0005). | Plan when the product is released. |

## B. Needs hardware or a person

| # | Item | How to resume |
|---|---|---|
| B1 | Windows 11, multiple monitors, per-monitor DPI above 100 % (checklist rows 11-12) | **Owner will test on a real Windows 11 PC.** Automated: the app starts and installs its hooks on a Windows Server 2025 (build 26100) image. | Run `docs/phase0-checklist.md` rows 11-15 there and send the failures. |
| B2 | **Send to phone** from a real phone, the Windows Firewall prompt, AP isolation, VPN | Open the QR on a phone on the same Wi-Fi. If it cannot connect, allow DropFlow in Windows Security > Firewall > Allow an app. |
| B3 | Word/PowerPoint/Excel opening the DOCX/PPTX/XLSX that DropFlow writes | Open a few outputs by hand. |
| B4 | The web app on Firefox/Safari/phones, a real Alt/Shift/Ctrl drag from Explorer into the page, folder drop, files over about 500 MB, PWA install, Lighthouse | Open `/ar/app` on those browsers; run `node e2e/run.mjs` against a deployed URL. |
| B5 | Touch and pen (checklist row 14), antivirus behaviour for the hooks (row 15) | Run the checklist. |

## C. Not built, on purpose, with the reason

| # | Item | Reason | Possible path |
|---|---|---|---|
| C1 | `pdf -> pdfa` | **Done in-process** (PDF/A-2b, veraPDF PASS). Text is rasterised, not selectable. | Optional: add an invisible text layer with an embedded font. |
| C2 | `heic -> pdf` | **Done** with a real HEVC HEIC fixture (BSD-3, source in tests/samples/README.md). | |
| C3 | CLI: speech to text, OCR, background removal, HTML to PDF | **Mostly done:** the CLI targets Windows, carries the AI tools and has `dropflow models list/download/delete` (download needs `--yes`). | HTML to PDF still needs its own STA thread in the CLI. |
| C4 | Browser: HEIC/TIFF input, GIF output, Office, OCR, Whisper, protect/unlock/sign/redact | No licence-clean browser decoder/encoder or library justified (ADR-0009). | A hint tile points to the Windows app; revisit per tool. |
| C5 | `Availability.NeedsModel` on `IConverter` | Today the consent dialog appears after the options dialog (ADR-0006). | Add the member and let the runner ask before the options. |
| C6 | Option kinds: file picker (sign picture path), multi-line text (redact regions/terms), multi-select (protect permissions) | **Partly done (2026-10-22):** the 7 image tools and 12 media tools now open dedicated visual editors (`IOptionsEditor`); the generic form (CLI, recipes, every other tool) still only has text, number, choice, bool, password, folder, range. | Add the kinds to `ToolOptionKind` and the dialog; editors for PDF tools (sign, redact) are still missing. |
| C7 | Localised result notes | Notes returned by converters (for example the redact summary) are English and long. | Return resource keys with arguments instead of text. |
| C8 | Windows ARM64 | **Built, unverified on hardware (2026-10-22):** PDFiumCore was replaced by PDFtoImage (PDFium for win-arm64 too); `.github/workflows/build-win-arm64.yml` publishes win-arm64 self-contained, bundles ARM64 FFmpeg (BtbN) and qpdf, and packs `DropFlow-win-arm64-Setup.exe` on the `win-arm64` channel. Tesseract has no Windows ARM64 build (left out unless the x64 build is bundled for emulation). | The workflow is now called from `release.yml` (job `win-arm64`, files attached by `attach-other-platforms`). Run `build-cross.yml` (includes `windows-11-arm`), then install on a real ARM64 PC. Refresh the BtbN FFmpeg pin when that dated autobuild is pruned. |
| C9 | Catalog `platforms` for web | **Done:** the flag now lists exactly the 20 tools the browser implements. | |
| C10 | Compare PDFs on files whose owner flags forbid copying text | Refused today (as the other text tools do), although the pixel diff alone would work. | Skip the word summary instead of refusing. |
| C11 | macOS / Linux packages of the app | The shared libraries and CLI logic now run there (Linux verified in a container; macOS via `build-cross.yml`), but no macOS `.app`/Linux package bundles FFmpeg, qpdf or Tesseract yet: they are found in Homebrew or the distribution (`EngineLocator`). HTML to PDF needs an installed Chrome/Chromium/Edge (or a WKWebView renderer from the Mac app). | Bundle engines under `Contents/Resources/engines` (macOS) or `engines/` (Linux): the locator already looks there. |

| C12 | **Web translation** (English <-> Arabic in the browser, `/app`) | Not built on purpose (2026-10-23). The two Opus-MT models are about 108 MB each; the web app would have to download them from a third party (Hugging Face) at run time, which needs the strict `/app` CSP `connect-src` opened beyond `self/blob/data` (today only the link-drop tool may fetch, and only with GET) and a new consent note, and it would run the networks in WASM (onnxruntime-web is already vendored for background removal). | Vendor the same four files per direction at build time like `u2netp` (`web/scripts/vendor.mjs`, SHA-256 pinned from `src/DropFlow.AI/Models/ModelCatalog.cs`), serve them from `/vendor/opus-mt/`, load them in a Web Worker with the vendored `onnxruntime-web` and port `MarianModel.Generate` (beam search with the merged decoder's key/value cache), `MarianTokenizer` (SentencePiece Unigram: needs a JS tokenizer or a small own Unigram implementation; `source.spm` is a protobuf) and `TextSegmenter` to TypeScript. Then add `txt.translate`/`img.translate`/`pdf.translate` to the catalog `platforms` for web, to the web manifest and to the no-upload test. Subtitle and Markdown parsers (`src/DropFlow.AI/Translation/Formats`) are plain string code and port directly. |
| C13 | **Translation on macOS, Linux and Android** | The translation code (`src/DropFlow.AI/Translation`) is platform-neutral and builds for `net10.0`, but the Avalonia desktop app (`src/DropFlow.Desktop`) does not reference the AI project yet (same as speech, OCR and cut-out), and `img.translate` needs an OCR engine there (Tesseract only). Android has no ONNX/Marian runtime in the app. | macOS/Linux: reference `DropFlow.AI` from `DropFlow.Desktop`, call `AiModule.Create`, reuse the consent flow with `ModelManager`; the overlay already works there as long as ImageMagick lays out Arabic (the Magick.NET build reports `raqm`; `ArabicShaper` is the fallback). Android: ONNX Runtime Mobile plus a Kotlin port of `MarianModel`/`MarianTokenizer` (about 108 MB download per direction, consent first). |
| C14 | Translation quality work | English to Arabic is the weaker direction (the Helsinki model scores BLEU 14 on the Tatoeba test set; Arabic to English 49). Better English to Arabic models exist (for example `Helsinki-NLP/opus-mt-tc-big-en-ar`, CC-BY-4.0, about 4x larger) but no ONNX export of it is published (checked 2026-10-23: `Xenova/` and `onnx-community/` have none; `Helsinki-NLP/opus-mt-tc-big-en-ar` is PyTorch only). | Export it with Hugging Face Optimum (`optimum-cli export onnx`, then int8-quantise and merge the decoder), host or pin the files, evaluate it against `tests/DropFlow.AI.Tests/RealTranslationTests.cs` and the samples in `docs/status.md`, add it as a third catalogue entry (`opus-mt-tc-big-en-ar`) and offer it as a `quality=best` choice. |

## Links and exact steps (keyed to the items above)

Repository: https://github.com/hamwimoustafa88-maker/dropflow · this file: https://github.com/hamwimoustafa88-maker/dropflow/blob/main/docs/deferred.md

- **A1 drag test:** checklist https://github.com/hamwimoustafa88-maker/dropflow/blob/main/docs/phase0-checklist.md · run `dotnet run --project src/DropFlow.Spike`
  (spike) or the real app `dotnet run --project src/DropFlow.App`. Keep the app running, drag a file out of Explorer, tap Shift/Ctrl/Alt.
- **A2 engines:** in the app Settings > Engines > Install, or by hand: `winget install --id Gyan.FFmpeg -e` · `winget install --id QPDF.QPDF -e` ·
  `winget install --id TheDocumentFoundation.LibreOffice -e` (≈ 100 / 25 / 350 MB). Sites: https://www.gyan.dev/ffmpeg/builds/ ·
  https://github.com/qpdf/qpdf/releases · https://www.libreoffice.org/download/download-libreoffice/ . Then `dotnet test DropFlow.sln`.
- **A3 models and Arabic OCR:** Settings > AI models > Download (shows size, source and licence first). Sources:
  Whisper https://huggingface.co/ggerganov/whisper.cpp · U²-Net https://huggingface.co/BritishWerewolf/U-2-Netp ·
  BiRefNet-lite https://huggingface.co/onnx-community/BiRefNet_lite-ONNX · UltraFace https://huggingface.co/Arm/ultraface-rfb-320-onnx-raspberry ·
  Opus-MT translation https://huggingface.co/Xenova/opus-mt-en-ar and https://huggingface.co/Xenova/opus-mt-ar-en (`dropflow models download opus-mt-en-ar --yes`).
  Tesseract: `winget install --id UB-Mannheim.TesseractOCR -e` (https://github.com/UB-Mannheim/tesseract/wiki); Arabic data
  `ara.traineddata` from https://github.com/tesseract-ocr/tessdata into Tesseract's `tessdata` folder.
- **A4 CI:** billing https://github.com/settings/billing · repo visibility https://github.com/hamwimoustafa88-maker/dropflow/settings (Danger Zone) ·
  runs https://github.com/hamwimoustafa88-maker/dropflow/actions · after fixing: `gh run rerun 39525285904` (or push a commit).
- **A5 release:** `dotnet tool install -g vpk` (https://docs.velopack.io/) then `pwsh installer/build.ps1 -Version 0.1.0`; winget submission via
  https://github.com/microsoft/winget-create and https://github.com/microsoft/winget-pkgs (templates in `installer/winget`). Runtime prerequisites:
  WebView2 https://developer.microsoft.com/microsoft-edge/webview2/ (preinstalled on current Windows).
- **A6 Vercel:** https://vercel.com/new > Import `hamwimoustafa88-maker/dropflow` > Root Directory `web` > Deploy (Framework Next.js, defaults).
  Or give the Claude Vercel connector access at https://claude.ai/settings/connectors (scope `moustafa-hamwis-projects`).
- **B1 Windows 11:** Windows 11 ISO https://www.microsoft.com/software-download/windows11 (for a VM), then the checklist.
- **B4 web checks:** `cd web; npm run build; npx next start --port 3200; node e2e/run.mjs http://localhost:3200` · Lighthouse
  https://developer.chrome.com/docs/lighthouse/overview .
- **C1 PDF/A:** Ghostscript https://ghostscript.com/ (AGPL, external process) · validator veraPDF https://verapdf.org/ .

## D. Known limits worth remembering
- Redaction by words uses the text layer only (scans need regions); rasterized pages lose selectable text, links and form fields.
- PDF crop keeps cropped-away content in the file; redact is the privacy tool.
- "Sign PDF" is a visible signature, not a cryptographic one.
- Wi-Fi sharing is IPv4 only and listens on every adapter (the token is the protection).
- Translation is machine translation: names, slang, dialects, idioms and very long sentences can come out wrong; English to Arabic is weaker than Arabic to English (C14). Files are translated sentence by sentence, so context across sentences is lost; Markdown emphasis markers split a sentence into separately translated pieces.
- `img.translate` overlay paints a patch of the surrounding colour over each paragraph: on photos with textured backgrounds the patch is visible, and a translation much longer than the original shrinks or extends sideways. Unknown scripts and mixed-direction posters are not handled.
- `pdf.translate` writes text and Markdown only; it does not rebuild the PDF layout, and scanned PDFs must be OCR'd first.
- `pdf.watermark` and `pdf.page-numbers` misplace on pages whose MediaBox does not start at 0,0 (a PDFsharp quirk; `pdf.sign` compensates).

