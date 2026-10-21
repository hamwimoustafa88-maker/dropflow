# Contributing to DropFlow

Thank you for helping. DropFlow welcomes contributors from everywhere, and especially Arab developers: issues, discussions and
pull-request descriptions may be written in **Arabic or English**. [القسم العربي في الأسفل](#المساهمة-في-dropflow-بالعربية).

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md). Security problems are **not** reported in public
issues — see [SECURITY.md](SECURITY.md).

## 1. Set up

Requirements: Windows 10/11 x64, [.NET 10 SDK](https://dotnet.microsoft.com/download), Node.js 24, PowerShell 7 (`pwsh`), Git.
Any editor with .NET 10 support works (Visual Studio, Rider, or VS Code with C# Dev Kit).

```powershell
git clone https://github.com/<you>/dropflow.git   # your fork
cd dropflow
node catalog/build.mjs        # must print "catalog: OK"
dotnet build DropFlow.sln
dotnet test DropFlow.sln      # one test: dotnet test --filter "FullyQualifiedName~Name"
pwsh tools/fetch-engines.ps1  # optional: pinned FFmpeg, qpdf, Tesseract into tools/engines (git-ignored)
cd web; npm install; npm run dev; npm run lint; npm test
```

Tests that need FFmpeg, qpdf, LibreOffice, WebView2 or an AI model are marked `[EngineFact]` / `[ModelFact]` / `[WebViewFact]` and
**skip visibly** when it is missing. That is expected; CI runs without them too. The website uses Next.js 16, which differs from older
versions: read `web/README.md` and `web/node_modules/next/dist/docs/` before changing it.

## 2. Find something to work on

- Issues labelled [`good first issue`](https://github.com/hamwimoustafa88-maker/dropflow/labels/good%20first%20issue) are small and
  self-contained; [`help wanted`](https://github.com/hamwimoustafa88-maker/dropflow/labels/help%20wanted) are larger.
- [docs/deferred.md](docs/deferred.md) lists postponed work with how to resume it, and [docs/status.md](docs/status.md) lists what is
  still unverified — testing on Windows 11, several monitors, high-DPI screens, real phones or Firefox/Safari is a very useful contribution.
- Comment on the issue before you start so two people do not do the same work. For a new feature, open an issue or a
  [Discussion](https://github.com/hamwimoustafa88-maker/dropflow/discussions) first.

## 3. Project rules (please read before writing code)

These come from [CLAUDE.md](CLAUDE.md) and the ADRs in [docs/adr/](docs/adr/). Reviews check them.

1. **The catalog is the single source of truth.** Every tool and conversion is declared in [`catalog/tools.json`](catalog/tools.json).
   After editing it run `node catalog/build.mjs` (must print OK) and commit the regenerated `docs/feature-catalog.md` and
   `web/src/data/catalog.json` — CI fails if they are stale. DropFlow must never offer less than Tangerine in a category (the script checks it).
2. **Every converter follows the `IConverter` contract** in [docs/writing-a-converter.md](docs/writing-a-converter.md): truthful
   `Supports`, options with defaults, `CancellationToken` end to end, progress, expected failures returned as `ConversionResult.Fail`.
3. **Never overwrite or delete an original.** Outputs go beside the source as `name (converted).ext` (via `OutputPathResolver`) or into
   a new folder for multi-file output. Undo deletes only what DropFlow produced.
4. **Privacy is the product.** Never send file contents, names or metadata over the network, on desktop or web. Network use is limited to
   user-approved downloads (models, updates, a link the user typed) and static web assets. Converters and tests never touch the network.
   Web processing runs in Web Workers; do not add `fetch`/XHR/WebSocket calls to the web app (a test scans for them).
5. **External programs** (FFmpeg, qpdf, Tesseract, LibreOffice) run as child processes through `ProcessRunner` with a timeout,
   cancellation and captured stderr. Never block the UI thread. UI projects never reference engine projects directly.
6. **Unlock PDF only removes protection with a password the user supplies.** No password cracking, ever.
7. **Tests: xUnit with plain `Assert`.** Do not add FluentAssertions (version 8+ is commercially licensed). Web: Vitest.
   Work **test-first**: write the failing test, make it pass, and for converters test success, cancellation, corrupt input, name
   collisions and an Arabic/Unicode path. One tool per pull request.
8. **Every user-visible string exists in Arabic and English.** Desktop: `src/DropFlow.Core/Resources/strings.en.json` and
   `strings.ar.json`; web: `web/messages/en.json` and `ar.json`. No hard-coded UI text. **Every view must work right-to-left.**
   Write Arabic that a native speaker would write, not a word-for-word translation.
9. **Dependencies:** open an issue before adding one. Prefer MIT, BSD, Apache-2.0, MPL-2.0, ISC, Zlib or LGPL; other GPL-3.0-compatible
   licences need the maintainer's agreement ([ADR-0013](docs/adr/0013-gpl3-open-source.md)). Not accepted: AGPL (PDFsharp stays at
   6.2.4 because 6.5+ is AGPL), proprietary or non-redistributable SDKs, non-commercial/research-only models (e.g. RMBG-1.4), and code
   copied from projects with incompatible licences. Record every new dependency in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
10. **Never commit binaries** (engines, models, installers). `tools/` holds scripts that download them with pinned SHA-256.
11. **Branding:** use DropFlow's own name, logo, colours and copy — never Tangerine's.
12. **C# style:** nullable enabled, file-scoped namespaces, async + `CancellationToken`, CommunityToolkit.Mvvm source generators,
    minimal code-behind. The desktop app is per-monitor DPI v2 aware and never runs elevated.
13. **Docs:** if you change behaviour, update [docs/status.md](docs/status.md) honestly (verified vs. unverified) and the relevant docs.

## 4. Commits: Conventional Commits + DCO sign-off

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/): `feat(pdf): add booklet layout`,
`fix(media): keep Arabic subtitle encoding`, `docs: ...`, `test: ...`, `ci: ...`.

Every commit must be **signed off** under the [Developer Certificate of Origin 1.1](https://developercertificate.org/) (DCO):

```powershell
git commit -s -m "fix(pdf): keep page rotation when cropping"
# adds:  Signed-off-by: Your Name <you@example.com>
```

The sign-off is your statement that you wrote the change (or otherwise have the right to submit it) and that it may be distributed
under the project's licence, **GPL-3.0-or-later**. There is **no CLA**: you keep the copyright of your contribution, and nobody — the
maintainer included — can relicense the project under closed terms. Forgot it? `git commit --amend -s` (last commit) or
`git rebase --signoff main` (whole branch), then force-push your branch. Use a real name or a consistent identity you are known by.

## 5. Pull requests

1. Fork, create a branch from `main` (`fix/pdf-crop-rotation`), keep the change focused (one tool or one fix).
2. Fill in the pull-request checklist. CI must be green: catalog check, Windows build and tests, website lint and build.
3. The maintainer reviews every pull request from a non-committer before it is merged (see [docs/code-signing-policy.md](docs/code-signing-policy.md)).
   Be ready for questions and small changes — it is not a judgement of you.
4. Screenshots or a short recording help for UI changes; please show both Arabic (RTL) and English.

---

<div dir="rtl">

## المساهمة في DropFlow (بالعربية)

شكراً لرغبتك في المساهمة! المشروع مفتوح للجميع، ونرحّب بالمطوّرين العرب خصوصاً: يمكنك كتابة الـ Issues والنقاشات ووصف طلبات الدمج
(Pull Requests) **بالعربية أو بالإنجليزية**. المشاركة تعني الالتزام بـ [ميثاق السلوك](CODE_OF_CONDUCT.md)، والثغرات الأمنية لا تُنشر في
Issues عامة بل تُبلَّغ بشكل خاص كما في [SECURITY.md](SECURITY.md).

### الإعداد

تحتاج: ويندوز 10/11 (x64)، و‎.NET 10 SDK، وNode.js 24، وPowerShell 7، وGit. أوامر البناء والاختبار هي نفسها في القسم الإنجليزي أعلاه
(`node catalog/build.mjs` ثم `dotnet build DropFlow.sln` ثم `dotnet test DropFlow.sln`). الاختبارات التي تحتاج FFmpeg أو qpdf أو نموذج ذكاء
اصطناعي تُتخطّى بشكل ظاهر إن لم تكن موجودة، وهذا طبيعي.

### من أين أبدأ؟

- المهام الموسومة `good first issue` صغيرة ومناسبة للبداية.
- اختبار التطبيق على ويندوز 11 أو على شاشات متعددة أو بدقة عرض عالية أو على هاتف حقيقي مساهمة قيّمة جداً — راجع [docs/status.md](docs/status.md).
- اكتب تعليقاً على الـ Issue قبل أن تبدأ، وافتح نقاشاً قبل أي ميزة جديدة كبيرة.

### أهم قواعد المشروع

1. **الكتالوج هو المصدر الوحيد**: كل أداة أو تحويل يُعرَّف في `catalog/tools.json`، وبعد تعديله شغّل `node catalog/build.mjs` وأضف الملفات المولّدة إلى الـ commit.
2. **كل محوّل يتبع عقد `IConverter`** الموضّح في [docs/writing-a-converter.md](docs/writing-a-converter.md).
3. **لا نكتب فوق الملف الأصلي ولا نحذفه أبداً**: الناتج يُحفظ بجانبه باسم مثل `name (converted).ext`.
4. **الخصوصية أولاً**: لا يُرسل محتوى أي ملف أو اسمه عبر الشبكة، لا في تطبيق ويندوز ولا في الويب.
5. **الاختبارات**: ‏xUnit مع `Assert` العادي (لا تضف FluentAssertions)، واكتب الاختبار الفاشل أولاً (TDD)، واختبر مساراً يحتوي أحرفاً عربية.
6. **النصوص بلغتين**: كل نص يظهر للمستخدم يجب أن يوجد بالعربية والإنجليزية (`src/DropFlow.Core/Resources/strings.ar.json` و`strings.en.json`،
   و`web/messages/ar.json` و`en.json`)، وكل واجهة يجب أن تعمل من اليمين إلى اليسار. اكتب عربية طبيعية لا ترجمة حرفية.
7. **المكتبات الجديدة**: اسأل في Issue أولاً، وسجّلها في `THIRD-PARTY-NOTICES.md`. لا نقبل AGPL ولا مكوّنات مغلقة ولا نماذج «للاستخدام غير التجاري فقط».
8. **فك قفل PDF** يكون فقط بكلمة مرور يعرفها المستخدم؛ لا كسر لكلمات المرور إطلاقاً.

### توقيع DCO على كل commit

نستخدم [شهادة منشأ المطوّر (DCO)](https://developercertificate.org/) بدلاً من اتفاقية ترخيص المساهمين (CLA). أضف `-s` عند كل commit:

<div dir="ltr">

```powershell
git commit -s -m "fix(pdf): keep page rotation when cropping"
```

</div>

هذا يضيف سطر `Signed-off-by: اسمك <بريدك>`، وهو إقرار بأنك كتبت التغيير أو تملك حق تقديمه، وبأنه يُنشر برخصة المشروع **GPL-3.0-or-later**.
تبقى حقوق النشر لك، ولا يستطيع أحد — ولا صاحب المشروع نفسه — تحويل المشروع إلى رخصة مغلقة. رسائل الـ commit تتبع
[Conventional Commits](https://www.conventionalcommits.org/) مثل `feat(pdf): ...` و`fix(media): ...`.

### طلب الدمج (Pull Request)

أنشئ فرعاً من `main`، واجعل التغيير مركّزاً (أداة واحدة أو إصلاح واحد)، واملأ قائمة التحقق في قالب طلب الدمج، وتأكد أن CI ناجح.
يراجع صاحب المشروع كل طلب دمج قبل قبوله، وقد يطلب تعديلات صغيرة — هذا جزء طبيعي من العمل وليس حكماً عليك.

</div>
