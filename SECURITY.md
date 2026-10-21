# Security policy

## Supported versions

DropFlow is young; only the **latest release** receives security fixes. Please update (the Windows app checks GitHub Releases, or
download from <https://github.com/hamwimoustafa88-maker/dropflow/releases/latest>) before reporting.

| Version | Supported |
|---|---|
| Latest release (currently 0.1.x) | Yes |
| Older releases | No |
| `main` branch | Best effort (fixes land here first) |
| Web app at https://dropflow.scouthub.dev | Yes (always the latest deployment) |

## Reporting a vulnerability

**Please do not open a public issue for a security problem.** Report it privately on GitHub:

1. Go to <https://github.com/hamwimoustafa88-maker/dropflow/security/advisories/new>
   (or the repository's **Security** tab → **Report a vulnerability**).
2. Describe the problem, the affected version (app, CLI or web), and steps or a file that reproduces it. Arabic or English is fine.

Only the maintainer can see the report. You can expect an acknowledgement within **7 days** and an assessment within **30 days**.
Fixes are released as soon as practical, and the advisory is published afterwards with credit to you unless you prefer to stay anonymous.
DropFlow is maintained by one person in their spare time, so these are goals rather than guarantees, and there is no bug bounty.

## What is in scope

DropFlow processes untrusted files, so these matter most:

- a crafted file that escapes the output folder (path traversal, "zip slip"), overwrites or deletes an original, or exhausts disk or memory (decompression bombs);
- a crafted file that runs code, or injects arguments into FFmpeg, qpdf, Tesseract or LibreOffice;
- **any way file contents, names or metadata leave the device** (the desktop app, the CLI or the web app) — that breaks DropFlow's core promise;
- the "Send to phone" local server being reachable or readable without the one-time link/token;
- model or update downloads that can be tampered with (the SHA-256 checks or Velopack's update feed);
- the web app's Content Security Policy or Web Worker isolation being bypassed.

Out of scope: vulnerabilities in third-party programs that DropFlow ships unmodified (FFmpeg, qpdf, Tesseract) or uses from the
system (WebView2) — please report those upstream; we will update the bundled version once a fix exists. Windows SmartScreen warnings
about the unsigned installer are known (see the README).

---

<div dir="rtl">

## سياسة الأمان (بالعربية)

**لا تفتح Issue عاماً للإبلاغ عن ثغرة أمنية.** أبلغ عنها بشكل خاص عبر GitHub من الرابط
<https://github.com/hamwimoustafa88-maker/dropflow/security/advisories/new> (أو تبويب **Security** ثم **Report a vulnerability**)،
واذكر الإصدار المتأثر وخطوات إعادة المشكلة، بالعربية أو بالإنجليزية. لا يرى البلاغ إلا صاحب المشروع.

نسعى للرد خلال 7 أيام وتقييم المشكلة خلال 30 يوماً، ونذكر اسمك عند نشر الإصلاح إلا إذا فضّلت عدم ذكره. الإصلاحات الأمنية تصدر لآخر إصدار فقط.
أهم ما يعنينا: أي طريقة تجعل محتوى ملفات المستخدم أو أسماءها تغادر جهازه، أو ملف مصمَّم يكتب خارج مجلد الناتج أو يستبدل الملف الأصلي
أو يشغّل أوامر عبر FFmpeg أو غيره.

</div>
