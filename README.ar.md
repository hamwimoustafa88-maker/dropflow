<div dir="rtl">

<p align="center">
  <img src="web/src/app/icon.svg" width="96" height="96" alt="شعار DropFlow">
</p>

<h1 align="center">DropFlow</h1>

<p align="center">
  <strong>اسحب الملف، اضغط مفتاحاً، وحوّله — كل ذلك على جهازك فقط.</strong><br>
  تطبيق لويندوز 10/11 + تطبيق ويب يعمل داخل المتصفح · واجهة عربية كاملة (من اليمين لليسار) وإنجليزية
</p>

<p align="center">
  <a href="https://github.com/hamwimoustafa88-maker/dropflow/actions/workflows/ci.yml"><img src="https://github.com/hamwimoustafa88-maker/dropflow/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-GPL--3.0--or--later-blue" alt="الرخصة: GPL-3.0-or-later"></a>
  <a href="https://github.com/hamwimoustafa88-maker/dropflow/releases/latest"><img src="https://img.shields.io/github/v/release/hamwimoustafa88-maker/dropflow?label=release" alt="آخر إصدار"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11%20%7C%20Web-0078d4" alt="المنصات: ويندوز 10/11 والويب">
</p>

<p align="center">
  <a href="https://dropflow.scouthub.dev/ar">الموقع</a> ·
  <a href="https://dropflow.scouthub.dev/ar/app">تطبيق الويب</a> ·
  <a href="https://github.com/hamwimoustafa88-maker/dropflow/releases/latest">تنزيل نسخة ويندوز</a> ·
  <a href="README.md"><strong>English</strong></a>
</p>

---

## ما هو DropFlow؟

ابدأ بسحب أي ملف، ثم اضغط مفتاحاً، فتظهر عجلة دائرية تحت المؤشر. أفلت الملف على أحد أجزائها، فيحوّله DropFlow أو يعالجه
**على جهازك** — لا يُرفع أي ملف إلى أي مكان، لا في تطبيق ويندوز ولا في المتصفح.

| المفتاح أثناء السحب | العجلة | أمثلة |
|---|---|---|
| **Shift** | التحويل | PNG → WebP، ‏MKV → MP4، ‏DOCX → PDF، ‏SRT → VTT |
| **Ctrl** | الأدوات | ضغط، قص، دمج PDF، قص الفيديو، تحويل الكلام إلى نص، OCR |
| **Alt** | الإجراءات | نسخ، نقل، مشاركة، نسخ المسار، فتح الطرفية، رف عائم، إرسال للهاتف |

يمكن تغيير المفاتيح من الإعدادات.

## المزايا

الأرقام من [`catalog/tools.json`](catalog/tools.json)، وهو المصدر الوحيد لقائمة الأدوات (القائمة الكاملة في
[docs/feature-catalog.md](docs/feature-catalog.md)): **428 تحويلاً · 63 أداة · 7 إجراءات على الملفات.**

- **الصور** — تحويل ملفات JPG وPNG وWebP وHEIC وTIFF وAVIF وBMP وGIF وSVG وICO، والضغط وتغيير الحجم والقص والتحرير والتعليق
  والكولاج وحذف الموقع وبيانات EXIF، وتوليد أيقونات ICO وFavicon، وقراءة رموز QR وإنشاؤها.
- **PDF** — دمج وتقسيم وترتيب الصفحات وتدوير وضغط، وعلامة مائية وترقيم صفحات واقتصاص وتوقيع مرئي، وإخفاء نهائي للمحتوى
  ومقارنة ملفين، وحماية بكلمة مرور وفتحها (بكلمة مرور تعرفها)، وإصلاح، وPDF/A-2b، وتحويل PDF إلى Word وPowerPoint وExcel وصور ونص وEPUB.
- **المستندات** — تحويل Word والجداول والعروض وMarkdown وHTML وEPUB وCSV/JSON إلى PDF وصور عبر متصفح WebView2 المدمج في ويندوز.
- **الصوت والفيديو** — تحويل وضغط (حتى إلى حجم مستهدف) وقص واقتصاص وتقسيم ودمج وتغيير السرعة وإزالة الصوت وتوحيد مستواه
  وإخفاء مقاطع بصفير وتصوير الصوت كموجة ودمج الترجمة في الفيديو، و**مُرمّز DropFlow** ‏(H.264/H.265/AV1 مع الترميز عبر كرت الشاشة ومسبقات جاهزة).
- **ذكاء اصطناعي محلي** — تحويل الكلام إلى نص بـ Whisper (يدعم العربية)، واستخراج النص OCR (‏Windows OCR وTesseract، عربي وإنجليزي)،
  وإزالة الخلفية، والإخفاء التلقائي للوجوه والنصوص. لا يُنزَّل أي نموذج إلا بعد موافقتك.
- **الأرشيف وأدوات عامة** — فك ZIP وTAR وGZ و7z وRAR، والضغط إلى ZIP، وبصمة الملف (Hash)، وBase64، وإعادة التسمية الجماعية.
- **الإجراءات** — نسخ ونقل ومشاركة ونسخ المسار وفتح الطرفية ورف عائم وإرسال الملف إلى هاتف على شبكة Wi-Fi نفسها (رمز QR).
- **الأتمتة** — وصفات جاهزة ومجلدات مراقَبة واستدعاء العجلة من الحافظة، وقائمة الزر الأيمن في المستكشف و«إرسال إلى»، وأداة سطر الأوامر `dropflow`.

**تطبيق الويب** ([‎/ar/app](https://dropflow.scouthub.dev/ar/app)) يشغّل 20 أداة من هذه داخل المتصفح (الصور وPDF والترجمات والأرشيف
والبصمات). أما الصوت والفيديو وملفات Office وOCR وتحويل الكلام إلى نص فهي في تطبيق ويندوز فقط.

## الخصوصية

الخصوصية هي جوهر المنتج: محتوى ملفاتك لا يغادر جهازك أبداً.

- تطبيق ويندوز يعمل دون إنترنت. لا يتصل بالشبكة إلا في هذه الحالات: البحث عن تحديث في GitHub Releases، وتنزيل نماذج الذكاء الاصطناعي
  بعد موافقتك، وتنزيل ملف من رابط تطلبه أنت، وخادم «إرسال للهاتف» على شبكتك المحلية ما دامت نافذته مفتوحة.
- تطبيق الويب يعالج الملفات داخل المتصفح (Web Workers وWebAssembly)، وسياسة أمان المحتوى (CSP) لا تسمح له بالاتصال إلا بموقعه نفسه،
  واختبار آلي يفحص الكود بحثاً عن أي اتصال شبكي.
- لا حسابات ولا إحصاءات تتبّع ولا إعلانات. التفاصيل في [سياسة الخصوصية](https://dropflow.scouthub.dev/ar/privacy).

## الحالة الحالية (بصراحة)

**الإصدار v0.1.0** هو أول إصدار عام (21 أكتوبر 2026). ما تم التحقق منه وما لم يُتحقق منه بعد مسجّل في
[docs/status.md](docs/status.md) (بالإنجليزية)، فاقرأه قبل الاعتماد على أي ميزة.

- المثبّت **غير موقّع رقمياً بعد**، لذلك يُظهر Windows SmartScreen تحذيراً عند التشغيل الأول: اضغط **More info** ثم **Run anyway**
  (أو «مزيد من المعلومات» ثم «تشغيل على أي حال»). نعمل على التوقيع المجاني عبر SignPath Foundation ([سياسة توقيع الكود](docs/code-signing-policy.md)).
- أفاد صاحب المشروع أن السحب مع المفاتيح يعمل في اختبار يدوي على ويندوز 10. على ويندوز 11 جرى اختبار آلي فقط (يبدأ التطبيق ويثبّت
  مراقبة الفأرة ولوحة المفاتيح)، ولم يُجرَ بعد اختبار يدوي على ويندوز 11 ولا على شاشات متعددة ولا بتكبير عرض أعلى من 100%.
- قرابة 2,900 اختبار آلي في ‎.NET إضافة إلى اختبارات الويب تنجح على جهاز التطوير، والاختبارات التي تحتاج FFmpeg أو qpdf أو Tesseract
  أو نماذج الذكاء الاصطناعي تُتخطّى بشكل ظاهر عند غيابها.
- لا توجد لقطات شاشة بعد — [الموقع](https://dropflow.scouthub.dev/ar) يعرض العجلة.

## بنية المشروع

| المسار | المسؤولية |
|---|---|
| `catalog/` | ملف `tools.json` (كل أداة وتحويل) و`build.mjs` (يتحقق منه ويولّد الوثائق وبيانات الموقع) |
| `src/DropFlow.Core` | تحميل الكتالوج، عقد `IConverter`، السجل، طابور المهام، نموذج العجلة، تسمية الملفات الناتجة، الإعدادات، السجل، الوصفات، منطق CLI |
| `src/DropFlow.Hooks` | آلة حالات السحب والضغط، ومراقبة الفأرة ولوحة المفاتيح على خيط مستقل |
| `src/DropFlow.Overlay` | نافذة العجلة (هدف إفلات لا يسرق التركيز)، نموذج الخيارات، الرف العائم |
| `src/DropFlow.App` | تطبيق WPF: أيقونة شريط المهام، الإعدادات، السجل والتراجع، الإشعارات، نافذة المُرمّز، التحديثات (Velopack) |
| `src/DropFlow.Engines.*` | المحوّلات حسب المجال: `Images` و`Media` و`Pdf` و`Archive` و`Web` ‏(WebView2) و`Common` (تشغيل البرامج المساعدة وإيجادها) |
| `src/DropFlow.AI` | Whisper وOCR وإزالة الخلفية والإخفاء التلقائي ومدير النماذج (مع التحقق بـ SHA-256) |
| `src/DropFlow.Cli` | أداة سطر الأوامر `dropflow` |
| `tests/` | مشاريع xUnit، مشروع لكل مشروع مصدر، ومولّدات ملفات العيّنات |
| `web/` | صفحة الهبوط وتطبيق الويب (Next.js، عربي/إنجليزي) |
| `installer/`، `.github/workflows/` | التحزيم بـ Velopack وقوالب winget وسير عمل CI والإصدار |
| `docs/` | الخطة، سجلات القرارات (ADR)، سجل الحالة، الأعمال المؤجلة، وثائق الإصدار والتوقيع |

## البناء من المصدر

المتطلبات: ويندوز 10/11 (x64)، و[‎.NET 10 SDK](https://dotnet.microsoft.com/download)، وNode.js 24، وPowerShell 7 ‏(`pwsh`)، وGit.

<div dir="ltr">

```powershell
git clone https://github.com/hamwimoustafa88-maker/dropflow.git
cd dropflow

node catalog/build.mjs                  # validate the catalog (must print OK)
dotnet build DropFlow.sln
dotnet test DropFlow.sln                # engine tests skip visibly when a program or model is missing
dotnet run --project src/DropFlow.App   # run the Windows app

# Optional: pinned, SHA-256-checked FFmpeg, qpdf and Tesseract (what the installer bundles) into tools/engines
pwsh tools/fetch-engines.ps1

# Web
cd web
npm install
npm run dev                             # http://localhost:3000
```

</div>

الأمر `tools/fetch-engines.ps1` اختياري: يُنزّل البرامج المساعدة بنسخ مثبّتة ويتحقق من بصمتها، وتوضع في `tools/engines` (لا تُرفع إلى Git).
ضعها في PATH أو بجانب التطبيق في مجلد `engines\` لتجربة ميزات الصوت والفيديو وPDF المتقدمة وOCR. تفاصيل الإصدار والتحزيم في [docs/release.md](docs/release.md).

## المساهمة

نرحّب بالمساهمات، ونرحّب بالمطوّرين العرب خصوصاً. ابدأ بقراءة [CONTRIBUTING.md](CONTRIBUTING.md) (فيه قسم بالعربية: الإعداد،
وقواعد المشروع، وتوقيع DCO)، وابحث عن المهام الموسومة
[`good first issue`](https://github.com/hamwimoustafa88-maker/dropflow/labels/good%20first%20issue). للأسئلة والأفكار استخدم
[Discussions](https://github.com/hamwimoustafa88-maker/dropflow/discussions)، ويمكنك الكتابة بالعربية. التزم بـ
[ميثاق السلوك](CODE_OF_CONDUCT.md)، وأبلغ عن الثغرات الأمنية بشكل خاص كما في [SECURITY.md](SECURITY.md).

خارطة الطريق: [docs/plan.md](docs/plan.md) (بالعربية) · الأعمال المؤجلة وكيفية استئنافها: [docs/deferred.md](docs/deferred.md) ·
كيف تضيف محوّلاً جديداً: [docs/writing-a-converter.md](docs/writing-a-converter.md).

## الرخصة

DropFlow برنامج حر: يمكنك إعادة توزيعه و/أو تعديله وفق شروط **رخصة جنو العمومية العامة (GNU GPL) الإصدار 3 أو (حسب اختيارك) أي
إصدار لاحق** ([LICENSE](LICENSE)، [ADR-0013](docs/adr/0013-gpl3-open-source.md)). حقوق النشر (c) 2026 ScoutHub والمساهمون. من تطوير فرق **ScoutHub**، ورئيس فريق التطوير **مصطفى الحموي** (لبنان) — واتساب [‎+961 70 863 903](https://wa.me/96170863903).

- **الاسم والشعار:** رخصة GPL تشمل الكود فقط، لا اسم «DropFlow» ولا شعاره. النسخ المعدّلة يجب أن تحمل اسماً وشعاراً مختلفين —
  راجع [TRADEMARK.md](TRADEMARK.md).
- **المكوّنات الخارجية** تبقى برخصها الخاصة: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- **FFmpeg** (نسخة برخصة GPL-3.0) يُشحن دون تعديل كبرنامج مستقل في مجلد `engines\` داخل المثبّت، مع رخصته وعرض مكتوب لتوفير
  الكود المصدري؛ الكود متاح على <https://ffmpeg.org/releases/> وعند الطلب عبر Issues في هذا المستودع.

## شكر وتقدير

يقوم DropFlow على جهود مشاريع كثيرة، منها: FFmpeg وqpdf وTesseract OCR وMagick.NET ‏(ImageMagick) وPDFsharp وPdfPig
وPDFium/PDFiumCore وOpen XML SDK وClosedXML وMarkdig وSharpCompress وZXing.Net وWhisper.net ‏(whisper.cpp) وONNX Runtime
وVelopack وCommunityToolkit.Mvvm وH.NotifyIcon وNext.js وReact وTailwind CSS وnext-intl وpdf-lib وpdf.js وfflate.
فكرة السحب والعجلة مستوحاة من تطبيق Tangerine للماك؛ وDropFlow مشروع مستقل لا يرتبط به.

</div>
