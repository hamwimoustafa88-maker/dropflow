# Deferred work — what is postponed, why, and how to resume

Everything that can be built and verified on the development machine has been. What is listed here is **postponed on purpose**:
it needs a decision, an account, a download the owner has not approved, hardware, or a real person at the keyboard.
For what is verified and what is not, see [status.md](status.md). Last updated: 2026-10-21.

## A. Needs the owner (decision, account or consent)

| # | Item | Why it is waiting | How to resume |
|---|---|---|---|
| A1 | **Phase 0 drag test** (`docs/phase0-checklist.md`, rows 1-17) | Needs a real person on a real desktop; the shell used for development ignores injected mouse/keyboard input, and the owner postponed running it outside the sandbox. This is the biggest open risk: drag detection in Explorer, the browser and other apps. | Tell Claude "run the drag test now" (it moves the mouse for about a minute), or run the checklist by hand and paste the spike's *Copy log* for any failure. Then write `docs/adr/0010-input-capture.md` with the four decisions the checklist asks for. |
| A2 | **Engines**: modern FFmpeg (Gyan.FFmpeg), qpdf, LibreOffice | We never download binaries without the owner's say-so. The app asks the user itself before installing (winget). | Approve, or install from Settings > Engines. Then run `dotnet test`: the 52 FFmpeg, 6 qpdf/LibreOffice tests stop skipping, and the real flags can be checked (qpdf and LibreOffice flags were written from knowledge and have never run). |
| A3 | **AI models** (Whisper, background removal, face detection) and `ara.traineddata` for Tesseract | Large third-party downloads need consent. The app asks with name, size, source and licence (ADR-0006). | Approve, or press Download in Settings > AI models. Then the 7 skipped AI tests can run and real transcription, cut-out and face-redaction quality can be measured (never measured so far). |
| A4 | **GitHub Actions** | The first run (2026-10-21) started no job: "recent account payments have failed or your spending limit needs to be increased". Account billing is the owner's. | Fix Billing & plans, or make the repository public (free Actions); then `gh run rerun` the latest run and fix whatever it finds. |
| A5 | **First release** (installer, GitHub Release, winget) | Needs: the repo public (or a decision to release privately), the Velopack packer (`dotnet tool install -g vpk`, a download), and ideally a code-signing certificate (unsigned builds trigger SmartScreen). | Follow `docs/release.md`. Measure the installer size (it now bundles ASP.NET Core for the phone-share server) and check the native libraries land in `publish/app`. |
| A6 | **Vercel** | The connector got 403 on the owner's personal scope; no CLI login on this machine. | Grant the connector access, or import the repo in the Vercel dashboard with Root Directory `web`. On the first deployment confirm that `public/sw.js` (generated after `next build`) is served. |
| A7 | **Landing page CTAs** ("Windows" and "Web" buttons) | They are inert "Soon" badges until a release exists and the repo is public. | After A5, switch them on in `web/src/components/SoonButton.tsx` and remove `robots: noindex` when ready to launch. |
| A8 | **Accounts, sign-up, payments** (Phase 9) | Deferred by decision (ADR-0005). | Plan when the product is released. |

## B. Needs hardware or a person

| # | Item | How to resume |
|---|---|---|
| B1 | Windows 11, multiple monitors, per-monitor DPI above 100 % (checklist rows 11-12) | Run the checklist there. |
| B2 | **Send to phone** from a real phone, the Windows Firewall prompt, AP isolation, VPN | Open the QR on a phone on the same Wi-Fi. If it cannot connect, allow DropFlow in Windows Security > Firewall > Allow an app. |
| B3 | Word/PowerPoint/Excel opening the DOCX/PPTX/XLSX that DropFlow writes | Open a few outputs by hand. |
| B4 | The web app on Firefox/Safari/phones, a real Alt/Shift/Ctrl drag from Explorer into the page, folder drop, files over about 500 MB, PWA install, Lighthouse | Open `/ar/app` on those browsers; run `node e2e/run.mjs` against a deployed URL. |
| B5 | Touch and pen (checklist row 14), antivirus behaviour for the hooks (row 15) | Run the checklist. |

## C. Not built, on purpose, with the reason

| # | Item | Reason | Possible path |
|---|---|---|---|
| C1 | `pdf -> pdfa` | No trustworthy engine: Ghostscript is AGPL and not in winget; qpdf cannot convert to PDF/A; LibreOffice only does it from documents. | Decide an engine (for example Ghostscript as an external AGPL process the user installs) and add a validator (veraPDF) to the tests. |
| C2 | `heic -> pdf` | No way to create a HEIC test file here, so it is not claimed. | Add a HEIC fixture and a decoder check, then register it. |
| C3 | CLI: speech to text, OCR, background removal, HTML to PDF | They need a model download or the Windows UI thread. | Add `dropflow models list/download` (with the same consent text), retarget the CLI to `net10.0-windows10.0.19041.0`, and give HTML to PDF its own STA thread. |
| C4 | Browser: HEIC/TIFF input, GIF output, Office, OCR, Whisper, protect/unlock/sign/redact | No licence-clean browser decoder/encoder or library justified (ADR-0009). | A hint tile points to the Windows app; revisit per tool. |
| C5 | `Availability.NeedsModel` on `IConverter` | Today the consent dialog appears after the options dialog (ADR-0006). | Add the member and let the runner ask before the options. |
| C6 | Option kinds: file picker (sign picture path), multi-line text (redact regions/terms), multi-select (protect permissions) | The generic options form only has text, number, choice, bool, password, folder, range. | Add the kinds to `ToolOptionKind` and the dialog. |
| C7 | Localised result notes | Notes returned by converters (for example the redact summary) are English and long. | Return resource keys with arguments instead of text. |
| C8 | Windows ARM64 | PDFiumCore ships win-x64/x86 natives only. | Release win-x64 first. |
| C9 | Catalog `platforms` for web | Several tools list `web` but are not implemented in the browser, so the flag overstates support (the wheel itself is registry-driven and never shows them). | Make the catalog flag match the web registry, or generate it from it. |
| C10 | Compare PDFs on files whose owner flags forbid copying text | Refused today (as the other text tools do), although the pixel diff alone would work. | Skip the word summary instead of refusing. |

## D. Known limits worth remembering
- Redaction by words uses the text layer only (scans need regions); rasterized pages lose selectable text, links and form fields.
- PDF crop keeps cropped-away content in the file; redact is the privacy tool.
- "Sign PDF" is a visible signature, not a cryptographic one.
- Wi-Fi sharing is IPv4 only and listens on every adapter (the token is the protection).
- `pdf.watermark` and `pdf.page-numbers` misplace on pages whose MediaBox does not start at 0,0 (a PDFsharp quirk; `pdf.sign` compensates).
