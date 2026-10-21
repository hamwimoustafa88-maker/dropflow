# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org)

> **Status (2026-10-21):** DropFlow is applying to the SignPath Foundation programme. Releases up to and including **v0.1.0 are
> unsigned**. Once the project is accepted, every Windows release built as described below will be signed, and this note will be removed.

This policy describes how DropFlow's Windows binaries are signed, by whom, and what users can rely on. Project home page:
<https://dropflow.scouthub.dev> · source: <https://github.com/hamwimoustafa88-maker/dropflow> · downloads:
<https://github.com/hamwimoustafa88-maker/dropflow/releases>.

## Team roles

DropFlow currently has a single maintainer, who holds all three roles. Every member must use multi-factor authentication for
GitHub and for SignPath.

| Role | Members | Responsibility |
|---|---|---|
| Committers and reviewers | [@hamwimoustafa88-maker](https://github.com/hamwimoustafa88-maker) (Moustafa Hamwi) | May change the source directly; reviews every pull request from anyone else before it is merged |
| Approvers | [@hamwimoustafa88-maker](https://github.com/hamwimoustafa88-maker) (Moustafa Hamwi) | Manually approves each signing request in SignPath after checking that it comes from a release tag of this repository |

Contributions from people outside this list reach the code only through pull requests that a reviewer has read and merged. Build
scripts and CI workflows (`.github/workflows/`, `installer/`, `tools/`) are source code and are reviewed the same way.

## What is signed

Only binaries that are **built by GitHub Actions on GitHub-hosted runners from this repository's source**, by
[`.github/workflows/release.yml`](../.github/workflows/release.yml) when a version tag (`v*`) is pushed:

- `DropFlow.exe` and the `DropFlow*.dll` assemblies of the Windows app;
- `dropflow.exe` and its `DropFlow*.dll` assemblies (command-line tool);
- the Velopack installer `DropFlow-win-Setup.exe`.

Signed files must carry the product name **DropFlow** and the release version (SignPath checks these attributes). Nothing is signed from a developer's machine, and nothing
is signed without a manual approval for that specific release.

## What is not signed

- **Third-party programs and libraries** are shipped exactly as their publishers released them and are **not signed by DropFlow**:
  FFmpeg/ffprobe, qpdf and Tesseract OCR in the installer's `engines\` folder, and third-party DLLs such as PDFium, ONNX Runtime,
  Magick.NET, whisper.cpp and the .NET runtime. Their sources and licences are listed in
  [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).
- AI model files, which are downloaded only after the user agrees and are verified by SHA-256, not by signature.
- The web app (<https://dropflow.scouthub.dev/en/app>), which runs in the browser and is not a signed binary.
- Forks and builds made by anyone else, even if they use DropFlow's code (forks must also use another name — see
  [TRADEMARK.md](../TRADEMARK.md)).

## Privacy

DropFlow processes files on the user's device and never sends file contents, file names or file metadata anywhere. This program
will not transfer any information to other networked systems unless specifically requested by the user or the person installing or
operating it. The only network activity of the Windows app is:

- an **update check** against GitHub Releases (GitHub sees the request and the IP address; nothing about the user's files is sent);
- **AI model downloads** from Hugging Face, only after the user agrees in a dialog that shows the name, size and source;
- **Open from link**: downloading one file from an address the user typed or pasted;
- **Send to phone**: a temporary server on the local network while its window is open, reachable only with the link/QR code;
- helper-program installation through Windows' winget, only if the user enabled it in Settings or pressed Install.

The full privacy policy, including the third-party services involved (GitHub, Hugging Face, Vercel for the website) and their own
policies, is at <https://dropflow.scouthub.dev/en/privacy> (Arabic: <https://dropflow.scouthub.dev/ar/privacy>).

## Installation and removal

The installer is per-user, needs no administrator rights and changes nothing system-wide without telling the user (the optional
Explorer context-menu entries and start-with-Windows are switched in Settings). DropFlow can be removed from Windows **Settings → Apps**
like any other program.

## Reporting a problem with a signed file

If you find a signed DropFlow binary that does not match this policy, or suspect a compromised build, report it privately as described
in [SECURITY.md](../SECURITY.md). The maintainer will investigate together with SignPath Foundation.

---

<div dir="rtl">

## ملخص بالعربية

- **التوقيع الرقمي** لملفات DropFlow على ويندوز سيكون مجانياً عبر SignPath.io وبشهادة صادرة عن SignPath Foundation، بعد قبول طلب المشروع.
  الإصدارات حتى v0.1.0 **غير موقّعة**.
- **من يوقّع:** صاحب المشروع ‎@hamwimoustafa88-maker هو المطوّر والمراجع والموافِق على كل طلب توقيع، ويجب أن يستخدم كل عضو المصادقة متعددة العوامل.
  أي مساهمة من شخص آخر تُراجع قبل دمجها.
- **ما يُوقَّع:** فقط ما تبنيه GitHub Actions من الكود المصدري لهذا المستودع: ‏`DropFlow.exe` و`dropflow.exe` ومكتباتهما `DropFlow*.dll` ومثبّت Velopack.
  لا يُوقَّع شيء من جهاز شخصي، ولا يُوقَّع أي إصدار دون موافقة يدوية.
- **ما لا يُوقَّع:** البرامج والمكتبات الخارجية (FFmpeg وqpdf وTesseract وغيرها) تُشحن كما أصدرها أصحابها دون توقيعنا.
- **الخصوصية:** البرنامج لا يرسل أي معلومات إلى أنظمة أخرى إلا بطلب من المستخدم (تنزيل نموذج، فتح رابط، إرسال للهاتف) أو للبحث عن تحديث في
  GitHub، ولا يرسل محتوى الملفات أو أسماءها أبداً. التفاصيل في [سياسة الخصوصية](https://dropflow.scouthub.dev/ar/privacy).

</div>
