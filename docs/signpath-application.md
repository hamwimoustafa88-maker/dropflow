# Applying to SignPath Foundation (free code signing)

Prepared 2026-10-21 for the owner. **You must apply yourself, with your own identity** — nobody else may submit the form or contact
SignPath on your behalf. Nothing has been submitted. This page gives you the steps, ready-to-paste answers, and an honest check of
every condition with evidence.

Sources read on 2026-10-21: the conditions at <https://signpath.org/terms> (the page is marked as a draft, so re-read it before
applying), <https://signpath.org/>, the application page <https://signpath.org/apply>, SignPath's GitHub guide
<https://docs.signpath.io/trusted-build-systems/github>, the artifact-configuration syntax <https://docs.signpath.io/artifact-configuration/syntax>,
and the action's `action.yml` at <https://github.com/SignPath/github-action-submit-signing-request>.
(<https://about.signpath.io/product/open-source> could not be read from here.)

## 1. What SignPath Foundation requires (summary)

For the free open-source subscription: no malware or unwanted programs; **an OSI-approved licence with no commercial
dual-licensing, for all components**; no proprietary components ("System Libraries" as defined by GPL-3.0 section 1 are allowed);
actively maintained; **already released in the form to be signed**; functionality described on the download page.

With a certificate issued by SignPath Foundation (they become the publisher) also: sign only your own project and **your own binaries**
(upstream binaries may be shipped unsigned, never signed by you); no hacking tools; no privacy compromise (transfers of user data
to unspecified systems need a privacy policy shown during installation and a way to turn them off); warn about system changes;
provide uninstallation; **MFA for every team member** on SignPath and the source repository; defined roles (authors/committers,
reviewers, approvers); **a published code signing policy** with the exact sentence *"Free code signing provided by SignPath.io,
certificate by SignPath Foundation"* and the phrase "Code signing policy" on the home page and the download/release page; file metadata
restrictions (every product name = the project name, the same product version in each build); builds from source in a verifiable way on
GitHub-hosted runners, with **manual approval of each signing request**.

## 2. Eligibility checklist

| # | Condition | State | Evidence / what to do |
|---|---|---|---|
| 1 | OSI licence, no commercial dual-licensing | **Met** | `LICENSE` is the verbatim GPL v3 text, project is GPL-3.0-or-later ([ADR-0013](adr/0013-gpl3-open-source.md)); contributions come in under DCO with no CLA, so dual-licensing is impossible. Check that the repository sidebar shows "GPL-3.0" (on 2026-10-21 the repository API still answered `NOASSERTION` while the community-profile API detected `gpl-3.0`; GitHub re-detects after a push). |
| 2 | No proprietary components | **Probably met — confirm three points with SignPath** | All NuGet/npm packages are MIT/BSD/Apache-2.0 ([THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)); PDFsharp pinned to 6.2.4 (MIT, 6.5+ is AGPL); no non-commercial models. Doubtful: **(a)** `Microsoft.Web.WebView2` ships Microsoft's closed-source `WebView2Loader.dll` under a BSD-style licence text — ask whether it counts as a System Library (the WebView2 runtime itself is part of Windows); **(b)** the HP "sRGB IEC61966-2.1" ICC profile embedded in PDF/A output is data under HP's permissive (non-OSI) terms, taken from Magick.NET at run time; **(c)** the UB Mannheim Tesseract build bundles many DLLs whose licences are only listed in `engines\Tesseract\doc` — audit them before applying. AI models are not in the installer (downloaded after consent; MIT/Apache-2.0). |
| 3 | No malware / unwanted programs | **Met** | No bundled offers, ads, telemetry or analytics. Explain the input hooks up front (see answers below): `WH_MOUSE_LL`/`WH_KEYBOARD_LL` are used only to detect a drag plus a modifier-key tap; antivirus heuristics may otherwise read them as a keylogger. |
| 4 | No hacking tools | **Met** | "Unlock PDF" only removes protection with a password the user knows; no cracking (project rule). |
| 5 | Actively maintained | **Met** | Daily commits in October 2026, CI green. |
| 6 | Already released in the form to be signed | **Met** | v0.1.0: <https://github.com/hamwimoustafa88-maker/dropflow/releases/tag/v0.1.0> (`DropFlow-win-Setup.exe`, portable zip, CLI zip), built by `release.yml`. |
| 7 | Functionality described on the download page | **Not yet met** | The website describes it, but the v0.1.0 **release notes are empty**. Edit the release on GitHub and add a short description plus a "Code signing policy" link (see section 5). |
| 8 | Own project, own binaries only | **Met (after this change)** | You own the repository. The new signing path in `.github/workflows/release.yml` sends only `DropFlow*.exe`/`DropFlow*.dll` and the installer; FFmpeg, qpdf, Tesseract, Velopack's `Update.exe` and third-party DLLs stay unsigned. The old `SIGN_PARAMS` path would have signed everything in the package, so it is disabled while SignPath is on. |
| 9 | Privacy | **Probably met — one doubt** | Policy: <https://dropflow.scouthub.dev/en/privacy>. File contents never leave the device. Doubt: the installed app checks GitHub Releases for updates **automatically** 45 s after start and there is **no setting to turn that off**; it sends no user data beyond the HTTP request, but adding a Settings switch "Check for updates automatically" would remove any argument. The installer does not display the privacy policy (Velopack has no such page). |
| 10 | Announce system changes | **Met** | Explorer context menu, Send To and start-with-Windows are changed only from Settings by the user. |
| 11 | Uninstallation | **Partly met** | Velopack provides uninstall from Windows Settings → Apps. But the app has **no uninstall hook**, so the Explorer context-menu verbs, the Send To shortcut and the start-with-Windows entry created from Settings are left behind. Fix in `src/DropFlow.App` (Velopack `OnBeforeUninstallFastCallback`) before applying. |
| 12 | MFA for all team members | **Owner must confirm** | Could not be checked from here (the API does not expose it). Enable at <https://github.com/settings/security> and in your SignPath account. |
| 13 | Roles defined | **Met** | [code-signing-policy.md](code-signing-policy.md): you are committer, reviewer and approver. |
| 14 | Code signing policy published, "Code signing policy" on home and download pages | **Partly met** | Policy file and README links exist once this change is pushed. Still to do: a "Code signing policy" link in the website footer (`web/`, pointing to the GitHub file or a site page) and in the release notes (future signed releases get it automatically from `release.yml`; add it to v0.1.0 by hand). |
| 15 | File metadata: product name = "DropFlow", same product version | **Not yet met** | .NET sets each assembly's product name to its own name (`DropFlow.Core`, `DropFlow.Engines.Pdf`…) and appends `+<commit>` to the product version. Add a root `Directory.Build.props` (owner decision — it affects every project): `<Product>DropFlow</Product>` and `<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>`. The Velopack `Setup.exe` metadata was not inspected — check it with `(Get-Item DropFlow-win-Setup.exe).VersionInfo`. |
| 16 | Verifiable build on GitHub-hosted runners, manual approval | **Met** | `release.yml` runs on `windows-latest`; the signing policy must require approval (section 4). |
| 17 | Security contact | **Owner action** | `SECURITY.md` points to private vulnerability reporting, which is currently **disabled**: enable it in Settings → Code security → Private vulnerability reporting. |
| 18 | Third-party text in the repository | **Check** | `docs/reference/tangerine-features.md` holds text copied from Tangerine's website (`docs/release.md` said to remove it before going public). It is not in any binary, but SignPath reviewers may ask about it. |

## 3. Steps for the owner

1. Fix or accept the open items above: release notes for v0.1.0 (row 7), MFA (12), private vulnerability reporting (17); preferably also
   rows 11 and 15, and decide on row 18.
2. Push this change so `docs/code-signing-policy.md` is public at
   <https://github.com/hamwimoustafa88-maker/dropflow/blob/main/docs/code-signing-policy.md>, and add a "Code signing policy" link to
   the website footer.
3. Open **<https://signpath.org/apply>** and fill in the form yourself (it is an embedded form; its exact questions could not be read
   from here, so use the answers sheet below and adapt). Read <https://signpath.org/terms> again first.
4. Wait for SignPath Foundation's reply. Answer their questions honestly; mention the doubtful points of row 2 yourself.
5. After acceptance, set up signing (section 4), then push a new tag (e.g. `v0.1.1`) and approve the two signing requests.

## 4. After acceptance: connect the release workflow

The workflow is ready and **disabled** until you set a repository variable. In SignPath (<https://app.signpath.io>):

1. Add the predefined **Trusted Build System "GitHub.com"** to the organisation and link it to the project; install the **SignPath GitHub App**
   for this repository (both described in <https://docs.signpath.io/trusted-build-systems/github>).
2. Create the project with slug `dropflow`, a signing policy `release-signing` that **requires approval** (approver: you), and two artifact
   configurations (SignPath validates the XML when you save; adjust if it reports an error):

   `binaries` — the zip uploaded by the workflow contains `app/` and `cli/`:
   ```xml
   <artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
     <parameters>
       <parameter name="version" required="true" />
     </parameters>
     <zip-file>
       <directory path="app">
         <pe-file-set>
           <include path="DropFlow.exe" product-name="DropFlow" product-version="${version}" />
           <include path="DropFlow*.dll" min-matches="1" max-matches="unbounded" product-name="DropFlow" product-version="${version}" />
           <for-each><authenticode-sign /></for-each>
         </pe-file-set>
       </directory>
       <directory path="cli">
         <pe-file-set>
           <include path="dropflow.exe" product-name="DropFlow" product-version="${version}" />
           <include path="DropFlow*.dll" min-matches="1" max-matches="unbounded" product-name="DropFlow" product-version="${version}" />
           <for-each><authenticode-sign /></for-each>
         </pe-file-set>
       </directory>
     </zip-file>
   </artifact-configuration>
   ```
   `installer`:
   ```xml
   <artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
     <parameters>
       <parameter name="version" required="true" />
     </parameters>
     <zip-file>
       <pe-file path="DropFlow-win-Setup.exe">
         <authenticode-sign />
       </pe-file>
     </zip-file>
   </artifact-configuration>
   ```
   (The metadata attributes only pass after row 15 is fixed. Whether `product-name`/`product-version` may sit on `<include>`, and the
   `min-matches`/`max-matches` names, follow SignPath's syntax page — verify there; this sample was not validated against SignPath.)
3. Create an API token for a user with **submitter** rights, then in GitHub → Settings → Secrets and variables → Actions:
   secret `SIGNPATH_API_TOKEN`, variable `SIGNPATH_ORGANIZATION_ID`. Only if you chose other slugs: variables `SIGNPATH_PROJECT_SLUG`,
   `SIGNPATH_SIGNING_POLICY_SLUG`, `SIGNPATH_BINARIES_CONFIGURATION_SLUG`, `SIGNPATH_INSTALLER_CONFIGURATION_SLUG`.
4. Push a tag. The run pauses twice (up to 2 hours each) until you approve in SignPath: first DropFlow's own binaries, then — after
   Velopack has packed them — the installer. It then checks every signature, publishes the release and adds the code-signing lines to
   the release notes. Without `SIGNPATH_ORGANIZATION_ID` all these steps are skipped and the release is built exactly as before.

Known limits: Velopack's `Update.exe` (an upstream binary) stays unsigned, so the update stub shows as unsigned; the portable zip contains
signed DropFlow binaries but no signed launcher. The flow was validated as YAML and by reading SignPath's documentation; it has **never
run** against a real SignPath account.

## 5. Answers sheet (ready to paste — adapt where marked)

| Question | Answer |
|---|---|
| Project name | DropFlow |
| Repository | https://github.com/hamwimoustafa88-maker/dropflow |
| Licence | GPL-3.0-or-later (all of DropFlow's own code; no dual licensing, no CLA — contributions under DCO) |
| Homepage | https://dropflow.scouthub.dev |
| Download page | https://github.com/hamwimoustafa88-maker/dropflow/releases |
| Code signing policy | https://github.com/hamwimoustafa88-maker/dropflow/blob/main/docs/code-signing-policy.md |
| Privacy policy | https://dropflow.scouthub.dev/en/privacy |
| Short description | DropFlow is a free file converter for Windows 10/11 and the web. While you drag a file, tapping Shift, Ctrl or Alt opens a radial wheel to convert it (images, audio, video, PDF, Office documents, subtitles, archives) or run a tool (merge/split/compress/redact PDFs, trim video, OCR, speech to text). Everything runs locally; files are never uploaded. Arabic and English UI. |
| Platform / binary type | Windows x64 desktop app (.NET 10, WPF) and CLI; Authenticode for PE files |
| Files to sign | `DropFlow.exe` and `DropFlow*.dll` (app), `dropflow.exe` and its `DropFlow*.dll` (CLI), `DropFlow-win-Setup.exe` (Velopack installer). Third-party programs (FFmpeg, qpdf, Tesseract) are shipped unmodified and unsigned by us. |
| Build system | GitHub Actions on GitHub-hosted `windows-latest` runners; workflow `.github/workflows/release.yml`, triggered by a version tag; packaging with Velopack (MIT) |
| Maintainers / roles | Moustafa Hamwi (GitHub @hamwimoustafa88-maker) — sole maintainer: committer, reviewer and approver. MFA enabled on GitHub and SignPath *(confirm before sending)*. |
| Release cadence | No fixed schedule; first release v0.1.0 on 2026-10-21, next releases as fixes and features land *(adjust)* |
| Number of users / downloads | New project, first public release 2026-10-21; download numbers not yet meaningful *(check the release page)* |
| Contact e-mail | *your own address* |
| Anything unusual | The app installs low-level mouse/keyboard hooks (`WH_MOUSE_LL`, `WH_KEYBOARD_LL`) on a dedicated thread only to notice that a file drag is in progress and a modifier key was tapped; it records nothing and sends nothing. It runs as a normal user, never elevated. It bundles GPL-3.0 FFmpeg and Apache-2.0 qpdf/Tesseract as separate programs. |

---

<div dir="rtl">

## ملخص بالعربية

- **رابط التقديم:** <https://signpath.org/apply> — يجب أن تقدّم بنفسك وباسمك؛ لم يُرسَل أي شيء نيابة عنك.
- **الشروط المستوفاة:** رخصة GPL-3.0 معتمدة من OSI، المشروع مُصدَر فعلاً (v0.1.0)، يُبنى على GitHub Actions، سياسة التوقيع مكتوبة،
  ومسار التوقيع في `release.yml` جاهز ومعطّل حتى تضبط المتغير `SIGNPATH_ORGANIZATION_ID`.
- **ما يجب أن تفعله قبل التقديم:** تفعيل المصادقة الثنائية (MFA) في GitHub، تفعيل الإبلاغ الخاص عن الثغرات، كتابة وصف في صفحة إصدار v0.1.0
  مع رابط «Code signing policy»، وإضافة الرابط نفسه في تذييل الموقع.
- **نقاط تحتاج قراراً منك:** ضبط اسم المنتج «DropFlow» ورقم الإصدار في كل الملفات (`Directory.Build.props`)، إزالة إدخالات قائمة الزر الأيمن
  و«إرسال إلى» وبدء التشغيل عند إلغاء التثبيت، خيار لإيقاف البحث التلقائي عن التحديثات، ومصير ملف `docs/reference/tangerine-features.md`.
- **نقاط مشكوك فيها اسأل عنها SignPath بصراحة:** ملف `WebView2Loader.dll` المغلق المصدر من مايكروسوفت، وملف ألوان sRGB من HP، ومكتبات Tesseract المرفقة.

</div>
