# DropFlow on macOS — feasibility (2026-10-21)

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

`.github/workflows/macos-feasibility.yml` runs the tests of these projects on GitHub's Apple-Silicon runner with Homebrew FFmpeg and qpdf;
its result is recorded in docs/status.md.

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
