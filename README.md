# DropFlow

**Drag a file, tap a key, drop — convert anything locally. Windows + web. Open source (GPL-3.0).**
**اسحب ملفاً، اضغط مفتاحاً، أفلته — حوّل أي ملف محلياً. ويندوز + ويب. مفتوح المصدر.**

DropFlow is an enhanced Windows/web counterpart of the Mac app Tangerine: a radial wheel appears while you drag a file, with
conversions (Shift), tools (Ctrl) and file actions (Alt). Files never leave your device. On top of Tangerine's feature set it adds
a full iLovePDF-style PDF suite, a pro media encoder, local speech-to-text (Arabic included), OCR, a floating shelf and automation.

DropFlow نسخة مطوّرة من تطبيق Tangerine للماك لويندوز والويب: عجلة دائرية تظهر أثناء سحب الملف للتحويل (Shift) والأدوات (Ctrl)
والإجراءات (Alt)، بلا رفع ملفات. وتضيف مجموعة PDF شاملة ومُرمّز وسائط وتحويل الكلام إلى نص (العربية) وOCR ورفاً عائماً وأتمتة.

**Status (2026-10-21):** the Windows app, the engines (images, media, PDF/Office, archives, local AI), the Encoder, automation,
the CLI and the landing page are built and tested (about 2,400 automated tests). What is **not** verified yet — drag detection in
other apps, Windows 11, the real qpdf/LibreOffice/modern-FFmpeg runs, AI models, a first release — is listed honestly in
[docs/status.md](docs/status.md), and what is postponed on purpose (with how to resume) in [docs/deferred.md](docs/deferred.md). Plan: [docs/plan.md](docs/plan.md) · tools: [docs/feature-catalog.md](docs/feature-catalog.md) ·
contributor and agent rules: [CLAUDE.md](CLAUDE.md).

## Build

```powershell
dotnet build DropFlow.sln
dotnet test DropFlow.sln            # engine-dependent tests skip visibly when FFmpeg, qpdf, LibreOffice or models are absent
dotnet run --project src/DropFlow.App
node catalog/build.mjs              # after editing catalog/tools.json
cd web; npm install; npm run dev    # landing page
```

License: [GPL-3.0-or-later](LICENSE) · Third-party components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
