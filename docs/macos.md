# DropFlow on macOS — feasibility (2026-10-21)

> **Update 2026-10-22:** the Mac (and Linux) app now exists as `src/DropFlow.Desktop` (Avalonia). Drag detection does **not** need
> the Accessibility / Input Monitoring permission after all: it polls `NSEvent` and the drag pasteboard instead of a `CGEventTap`.
> Architecture, packaging (ad-hoc signed DMG) and what is still unverified: [desktop-mac-linux.md](desktop-mac-linux.md).

Short answer: **possible, and about half of the code already runs on a Mac**; the other half (the parts that touch the operating system)
must be written again for macOS. Tangerine itself is Mac-only, so a Mac DropFlow competes with it directly.

## What already works on macOS (net10.0, no Windows APIs)
| Project | Mac status | Notes |
|---|---|---|
| DropFlow.Core (catalog, registry, queue, wheel model, runner, recipes, CLI logic) | portable | pure .NET 10 |
| Engines.Common (process runner, engine locator) | portable | locator needs macOS paths (`/opt/homebrew/bin`, app bundle `Contents/Resources/engines`) |
| Engines.Images (Magick.NET) | portable | Magick.NET ships macOS arm64/x64 natives |
| Engines.Media (FFmpeg) | portable | FFmpeg runs on macOS; `h264_videotoolbox` / `hevc_videotoolbox` would be the Mac hardware encoders |
| Engines.Pdf (PDFsharp, PdfPig, PDFium, OpenXml, ClosedXML) | mostly | **PDFiumCore ships Windows natives only** → use the macOS builds from bblanchon/pdfium-binaries |
| Engines.Archive (SharpCompress, ZXing, QR, hashing) | portable | the phone-share server (Kestrel) is portable |
| AI: Whisper.net, ONNX Runtime | portable | both ship macOS natives (Whisper can use Metal/Core ML); Windows OCR must be replaced (below) |

### Measured on Apple Silicon (GitHub `macos-26-arm64`, macOS 26.6, Homebrew FFmpeg 9.0.2 + qpdf 12.4.2, 2026-10-21)
`.github/workflows/macos-feasibility.yml`, run 39600303132 — the shared tests compile and run on a Mac unchanged:

| Test project | Passed | Failed | Why it fails |
|---|---|---|---|
| Core | 310 / 326 | 16 | tests and code assume Windows paths (`C:\…`) and Windows named pipes (single instance) |
| Engines.Common | 9 / 26 | 17 | the locator looks for `ffmpeg.exe`, `qpdf.exe`… and Windows install folders |
| Engines.Images (Magick.NET) | **74 / 74** | 0 | — works as is |
| Engines.Media | 660 / 712 | 0 (52 skipped) | the 52 real-FFmpeg tests skipped because the locator did not find `ffmpeg` (no `.exe`) |
| Engines.Pdf | 417 / 714 | 290 | PDFiumCore ships Windows natives only (as predicted) |
| Engines.Archive | — | — | hung until the 6-hour job limit (probably the phone-share LAN test or a Windows-only process path); not diagnosed |

So: image work is ready today; making the engine locator find Unix executable names and adding macOS PDFium natives would turn most
red tests green; the archive hang needs a look on a real Mac.

### Platform layer (2026-10-22) — what was fixed, and the Linux result
Every failure above was addressed; the shared libraries now target Windows x64/ARM64, macOS (arm64/x64) and Linux (x64/arm64):

| Was failing | Fix |
|---|---|
| Windows paths in tests | test data goes through `TestPaths.P(...)` (`C:\a` → `/C/a` off Windows); sheet names are cleaned with the Windows rules on every OS so outputs copy across systems |
| Named pipes (single instance) | the next pipe server is created before the current one is closed: on Unix a .NET named pipe is a Unix socket whose listener closed with the last instance, dropping launches that connected meanwhile |
| `ffmpeg.exe`, `qpdf.exe`… | `EngineInfo.ExecutableNames` follows the OS; `EngineLocator` also searches `engines/` next to the app, `../Resources/engines` in a `.app`, `/opt/homebrew/bin`, `/usr/local/bin`, `/usr/bin`, MacPorts, snaps, LibreOffice's bundle |
| PDFiumCore = Windows natives only | **PDFtoImage** (approved) draws the pages; its packages carry PDFium for win-x64/x86/arm64, linux-x64/arm64 and osx-x64/arm64. Page count, sizes and permission flags use five direct PDFium calls under one process-wide lock (`NativeLocks.Pdfium`) |
| (found on Linux) PDFsharp cannot draw text off Windows | `SystemFontResolver` reads installed TrueType fonts (Arial on macOS, DejaVu Sans on Linux; both carry Arabic) |
| (found on Linux) Magick.NET text needs a font file off Windows | `FontFiles.DefaultSans()` for the annotate tool |
| (found on Linux) free-memory query was `kernel32` only (Whisper model choice crashed) | `/proc/meminfo` on Linux, `vm_stat` on macOS |
| Windows OCR, Windows.Data.Pdf | AI multi-targets `net10.0-windows10.0.19041.0;net10.0`; `Platform/Windows` vs `Platform/Unix`. Off Windows: Tesseract (ara+eng) and PDFium; a host may inject Apple Vision through `IWindowsOcr` |
| WebView2 HTML → PDF | `ChromiumHtmlPdfRenderer` (headless Chrome/Chromium/Edge/Brave, network blocked unless allowed) behind the existing `IHtmlPdfRenderer`, plus the portable `HtmlToPdfConverter`; a WKWebView renderer can be plugged into the same interface. No browser → a clear "install Chrome or Chromium" message |
| Archive hang | not reproduced on Linux (561 tests in 30-60 s). The only unbounded waits were in the phone-share tests (a raw socket read and a download read after Stop): both are now bounded (30 s / 60 s), and `build-cross.yml` runs every project with `--blame-hang-timeout 10m` and a 75-minute job limit, so a Mac hang now fails with a dump instead of burning six hours. **Root cause on macOS still unverified** |

Linux, measured in `mcr.microsoft.com/dotnet/sdk:10.0` (Ubuntu 24.04, x64) with FFmpeg 6.1.1, qpdf 11.9.0, Tesseract 5.3.4 (+ara): see
`docs/status.md` for the counts — every project passes; skips are Windows-only APIs, models that were not present and LibreOffice/7-Zip/WinRAR.
macOS and the ARM64 runners are covered by `.github/workflows/build-cross.yml` (run it from the Actions tab); they were not run from here.

## What must be rebuilt for macOS
| Windows piece | macOS replacement | Effort |
|---|---|---|
| `DropFlow.Hooks` (WH_MOUSE_LL / WH_KEYBOARD_LL) | `CGEventTap` or `NSEvent.addGlobalMonitorForEvents` for mouse-drag + modifier taps, plus `NSPasteboard(name: .drag)` to know a **file** is being dragged. Needs the user to grant **Accessibility / Input Monitoring** permission | high — the core of the product, same risk as Phase 0 on Windows |
| `DropFlow.Overlay` + `DropFlow.App` (WPF) | **Avalonia UI** (MIT, .NET, runs on Windows and macOS) for the wheel, settings, history; a non-activating floating `NSPanel` as drop target; `NSStatusItem` for the menu-bar icon | high — but moving Windows to Avalonia too would give one UI for both |
| `Engines.Web` (WebView2 HTML → PDF) | `WKWebView.createPDF` (macOS 11+) | medium |
| Windows OCR (`Windows.Media.Ocr`) | Apple **Vision** (`VNRecognizeTextRequest`; check Arabic support on the target macOS version) + the bundled Tesseract (has `ara`) | medium |
| Explorer verbs, Send To, startup registry | Finder **Quick Actions / Services**, Login Items (`SMAppService`) | medium |
| Velopack installer (Windows) | Velopack also builds macOS `.pkg` + updates | low |

## What it costs outside the code
- **A Mac** (Apple Silicon) for development and manual testing; GitHub's macOS runners are free for this public repository for automated tests.
- **Apple Developer Program, 99 USD / year**: required for a *Developer ID* signature and **notarization**. Without it macOS Gatekeeper blocks
  the app ("cannot be opened because the developer cannot be verified"). SignPath does not sign macOS apps.
- Homebrew cask distribution is possible once notarized.
- **Owner decision (2026-10-21): no paid Apple programme.** The Mac build would ship unsigned (ad-hoc signed only). macOS then blocks the
  first launch; the user opens it once with right-click → Open, or System Settings → Privacy & Security → "Open Anyway". The download page
  and README must explain this, and Homebrew cask distribution is not possible.

## Recommended path
1. Keep the shared libraries portable (CI job above). Fix the PDFium natives and the engine locator paths for macOS (small).
2. Ship the **CLI** for macOS first (`dropflow convert …`): it only needs the shared libraries — a useful product for developers within weeks.
3. Prototype the hard part on a real Mac: global drag detection with `CGEventTap` + drag pasteboard, and the floating drop panel.
   Go/no-go exactly like Windows Phase 0.
4. Then decide the UI: Avalonia for both systems (one codebase) or native SwiftUI for the Mac.
Rough size: CLI 1-2 weeks; full Mac app 2-3 months for one developer, plus the Apple fee.

## الخلاصة بالعربية
تشغيل DropFlow على ماك **ممكن**. تقريباً نصف الكود (النواة وكل محركات التحويل) يعمل على ماك كما هو، والنصف الآخر المرتبط بنظام التشغيل
يحتاج إعادة كتابة: التقاط السحب ومفاتيح التعديل (يحتاج إذن Accessibility)، والعجلة والواجهة (عبر Avalonia)، وتحويل HTML إلى PDF (WKWebView)،
وOCR (Apple Vision). يلزم جهاز ماك للاختبار. **بقرار المالك لا اشتراك مدفوع مع Apple:** يُوزَّع التطبيق دون توقيع Apple، فيفتحه المستخدم أول مرة بالنقر بزر الفأرة الأيمن ثم «فتح» (أو من إعدادات الخصوصية والأمان ← «فتح على أي حال»). أقترح البدء بأداة سطر الأوامر
لماك، ثم نموذج أولي لالتقاط السحب على جهاز ماك حقيقي.
