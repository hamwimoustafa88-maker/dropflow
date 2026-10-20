# DropFlow — خطة المشروع الكاملة

Oct 19, 2026 · @Moustafa

DropFlow نسخة ويندوز من فكرة Tangerine: تسحب ملفاً، تضغط مفتاحاً، فتظهر عجلة دائرية فوق كل التطبيقات لتحويل الملف أو معالجته محلياً بلا رفع إلى أي خادم، مع إضافات لا يملكها الأصل: ذكاء اصطناعي محلي، دعم عربي كامل، أتمتة، ورف عائم.

## الخلاصة والقرارات الأساسية

الفكرة قابلة للتنفيذ بالكامل على ويندوز 10 و11، وبأدوات مجانية فقط في المرحلة الأولى. Tangerine متاح لأجهزة ماك فقط ولا توجد له نسخة ويندوز أو ويب، فالسوق على ويندوز مفتوح.

| القرار | الاختيار | السبب |
| --- | --- | --- |
| اللغة والإطار | C# على ‎.NET 10 + WPF | نافذة شفافة Topmost بلا إطار تعمل في WPF بسطر واحد وبسرعة، وتدعم ويندوز 10 و11 بنفس الكود. WinUI 3 أصعب في الشفافية الكاملة. |
| شكل الواجهة | مكتبة WPF-UI (Fluent) | مظهر ويندوز 11 الحديث داخل WPF، ومجانية MIT. |
| التقاط السحب | Low-Level Mouse + Keyboard Hooks | الطريقة الوحيدة لمعرفة أن المستخدم يسحب ويضغط مفتاحاً في أي تطبيق. |
| استقبال الملف | العجلة نفسها هدف إفلات (Drop Target) | ويندوز يسلّم قائمة الملفات للعجلة عند دخول السحب إليها، فلا نحتاج لتخمين الملف المسحوب. |
| المحركات | FFmpeg، Magick.NET، QPDF/PDFium، LibreOffice headless، 7-Zip | كلها مجانية وتعمل دون إنترنت. |
| الذكاء المحلي | ONNX Runtime + Whisper.net + Windows OCR مع Tesseract احتياطياً | يعمل على الجهاز، والنماذج تُحمَّل عند الطلب فقط. |
| التوزيع | مثبّت Velopack أو Inno Setup + تحديث تلقائي | مجاني، ويضع الاختصار ويشغّل البرنامج مع ويندوز. |

المبدأ الحاكم: كل محرك يعمل كعملية منفصلة (CLI) أو مكتبة معزولة خلف واجهة موحّدة `IConverter`، حتى نستبدل أي محرك لاحقاً دون لمس الواجهة.

## الميزات: ما نأخذه وما نضيفه

نأخذ من Tangerine ثلاث عجلات (تحويل، أدوات، إجراءات) ونموذج «اسحب ثم اضغط مفتاحاً»، ونضيف فوقها ما يميّز DropFlow. المرحلة هنا تشير إلى خارطة الطريق أدناه.

| الميزة | المصدر | المرحلة |
| --- | --- | --- |
| عجلة التحويل (Shift أثناء السحب): صور، صوت، فيديو | Tangerine | 1 |
| عجلة الإجراءات (Ctrl): نسخ، نقل، نسخ المسار، فتح Terminal، مشاركة | Tangerine | 1 |
| بديل AirDrop: نافذة المشاركة في ويندوز (Nearby Sharing) | Tangerine (مُكيَّف) | 1 |
| فتح العجلة بـ Enter على ملف محدد في المستكشف + أسهم لوحة المفاتيح | Tangerine | 1 |
| المعالجة الجماعية مع شريط تقدم دائري على العجلة | فكرتك | 1 |
| عجلة الأدوات (Alt): ضغط، قص، دمج، تقسيم، تنظيم صفحات PDF | Tangerine | 2 |
| تحويل المستندات والعروض والجداول وPDF ← Word | Tangerine | 2 |
| الأرشيف: ZIP / 7z / فك RAR | Tangerine + إضافة 7z | 2 |
| الرف العائم المؤقت المثبّت على حافة الشاشة | فكرتك | 2 |
| تخصيص أزرار العجلة لكل نوع ملف | فكرتك | 2 |
| أدوات المطورين: Base64، هاش MD5/SHA256، لوحة الألوان، WebP/AVIF بحجم محدد | فكرتك | 2 |
| OCR عربي من الصور وPDF الممسوح | فكرتك | 3 |
| تفريغ الصوت والفيديو إلى SRT ونص بـ Whisper | فكرتك | 3 |
| إزالة الخلفية محلياً | فكرتك | 3 |
| واجهة عربية كاملة RTL + إنجليزية | إضافة مقترحة | 1 |
| تشغيل العجلة بهزّ الملف أو سحبه لحافة الشاشة بدل المفتاح | إضافة مقترحة | 2 |
| معاينة قبل التطبيق: الحجم المتوقع والأبعاد | إضافة مقترحة | 2 |
| سجل العمليات مع «تراجع» وفتح مجلد الناتج | إضافة مقترحة | 2 |
| إعادة تسمية جماعية بأنماط (تاريخ، ترقيم، استبدال) | إضافة مقترحة | 2 |
| حذف بيانات EXIF وموقع GPS من الصور قبل النشر | إضافة مقترحة | 2 |
| ضغط فيديو إلى حجم مستهدف (مثلاً لواتساب أو البريد) | إضافة مقترحة | 2 |
| توليد أيقونات ICO وFavicon بكل المقاسات من صورة واحدة | إضافة مقترحة | 2 |
| قراءة وتوليد رموز QR | إضافة مقترحة | 2 |
| الحافظة كمصدر: اختصار يحوّل الصورة أو النص المنسوخ مباشرة | إضافة مقترحة | 3 |
| وصفات متسلسلة: تصغير ← WebP ← ضغط ZIP بخطوة واحدة | إضافة مقترحة | 4 |
| مجلدات مراقَبة: كل HEIC يصل إلى Downloads يتحول تلقائياً إلى JPG | إضافة مقترحة | 4 |
| إرسال للهاتف عبر رمز QR على شبكة Wi-Fi المحلية | إضافة مقترحة | 4 |
| أداة سطر أوامر `dropflow` تستخدم نفس المحركات | إضافة مقترحة | 4 |
| نسخة ويب PWA تعمل داخل المتصفح بلا رفع | إضافة مقترحة | 4 |

أقوى نقاط التميّز أمام Tangerine: الدعم العربي (واجهة وOCR وتفريغ)، الأتمتة (وصفات ومجلدات مراقبة)، والذكاء المحلي.

## البنية التقنية

العجلة لا تحتاج لمعرفة الملف المسحوب مسبقاً: ويندوز يسلّمها الملفات لحظة دخول السحب إليها، ثم يتولى طابور خلفي تشغيل المحركات.

&#91;embedded content: مسار الملف داخل DropFlow · 6 خطوات\]

تُقرأ من اليمين إلى اليسار ثم للأسفل. الخطوة الأولى (المميّزة) هي الأصعب تقنياً، لذلك تُثبَّت في تجربة المرحلة 0 قبل أي شيء آخر؛ والمداخل الأخرى تصل مباشرة إلى سجل الأدوات.

## حزمة التقنيات المجانية وتراخيصها

كل ما يلي مجاني، لكن الترخيص يحدد هل يمكن بيع DropFlow لاحقاً أم لا. القاعدة: المكتبات المدمجة في الكود يجب أن تكون MIT أو Apache أو LGPL، أما أدوات GPL/AGPL فتُشغَّل كبرامج خارجية منفصلة أو تُستبدل.

| المجال | الأداة | الترخيص | طريقة الربط | ملاحظة |
| --- | --- | --- | --- | --- |
| الصور | Magick.NET (ImageMagick) | Apache 2.0 | NuGet داخل التطبيق | الخيار الرئيسي للصور |
| الصور السريعة | NetVips (libvips) | MIT + LGPL | NuGet | أسرع للدفعات الكبيرة؛ اختياري |
| الفيديو والصوت | FFmpeg | LGPL (نسخة LGPL) | ملف ffmpeg.exe خارجي | تجنّب نسخ GPL إن أردت البيع |
| PDF: دمج، تقسيم، تدوير، ضغط | QPDF | Apache 2.0 | CLI خارجي | بديل آمن لـ pdfcpu |
| PDF إلى صور ونص | PDFium عبر PDFiumCore | BSD/Apache | NuGet | محرك Chrome نفسه |
| Office و PDF ← Word | LibreOffice headless | MPL 2.0 | CLI خارجي (تحميل اختياري) | ثقيل الحجم، يُحمَّل عند أول استخدام |
| Markdown ومستندات | Pandoc | GPL | CLI خارجي منفصل | مقبول كبرنامج منفصل |
| الأرشيف | SharpCompress + 7za.exe | MIT / LGPL | NuGet + CLI | فك RAR مسموح، إنشاؤه غير مسموح |
| OCR | Windows.Media.Ocr | مدمج في ويندوز | WinRT API | يحتاج حزمة OCR العربية مثبتة |
| OCR احتياطي | Tesseract + بيانات ara | Apache 2.0 | NuGet | عند غياب حزمة ويندوز العربية |
| التفريغ الصوتي | Whisper.net (whisper.cpp) | MIT | NuGet + نموذج يُحمَّل عند الطلب | العربية تحتاج نموذج small أو أكبر |
| إزالة الخلفية | ONNX Runtime + نموذج U²-Net أو BiRefNet | MIT / Apache | NuGet + ملف ‎.onnx | بديل rembg داخل C# |
| QR | ZXing.Net | Apache 2.0 | NuGet | قراءة وتوليد |
| الهاش وBase64 | ‎.NET المدمج | — | — | بلا مكتبات |
| الواجهة | WPF-UI | MIT | NuGet | مظهر Fluent |
| MVVM | CommunityToolkit.Mvvm | MIT | NuGet |  |
| التحديث والتثبيت | Velopack | MIT | أداة بناء | تحديث تلقائي من GitHub Releases |

تحذيرات ترخيص يجب احترامها:

- MuPDF وGhostscript وPyMuPDF ترخيصها AGPL: لا تُدمج إن كان هناك نية للبيع.
- نموذج RMBG-1.4 لإزالة الخلفية للاستخدام غير التجاري فقط: استخدم U²-Net أو BiRefNet.
- مكتبة Sharp خاصة بـ Node.js ولا تناسب تطبيق C#؛ استُبدلت بـ Magick.NET وNetVips.
- برنامج File Converter المفتوح (GPL v3) مرجع جيد للتعلّم فقط؛ نسخ كوده يُلزم المشروع بـ GPL.

## تحديات ويندوز 10 و11 وحلولها

أصعب جزء في المشروع ليس التحويل بل التقاط السحب في أي مكان؛ لذلك يبدأ المشروع بتجربة تثبت هذا الجزء على النظامين قبل أي شيء آخر.

| التحدي | ما يحدث | الحل |
| --- | --- | --- |
| لا يوجد حدث «بدأ السحب» في ويندوز | النظام لا يخبر التطبيقات أن مستخدماً يسحب ملفاً في المستكشف | Mouse Hook يرصد ضغط الزر الأيسر مع تحرك أكبر من مسافة السحب، ثم Keyboard Hook يرصد المفتاح أثناء ذلك |
| معرفة الملف المسحوب | الـ Hook يعرف الحركة لا الملف | العجلة تظهر تحت المؤشر وتكون هدف إفلات؛ عند دخول السحب إليها يسلّمها ويندوز قائمة الملفات فتعرض الصيغ المناسبة |
| تعارض المفاتيح | في المستكشف Shift أثناء السحب = نقل، Ctrl = نسخ، Alt = اختصار | نستخدم «نقرة» على المفتاح ثم تحريره كما يفعل Tangerine، والإفلات يتم على العجلة لا على المستكشف؛ مع إمكانية تغيير المفاتيح من الإعدادات |
| مهلة الـ Hooks | ويندوز يزيل الـ Hook إن تأخر في الرد | الـ Hook على خيط مستقل بحلقة رسائل خاصة، ولا يفعل إلا إرسال حدث سريع |
| التطبيقات بصلاحية المسؤول | ويندوز يمنع السحب من تطبيق مرتفع الصلاحية إلى تطبيق عادي | نوثّق ذلك للمستخدم؛ لا نشغّل DropFlow كمسؤول |
| شاشات متعددة ودقة مختلفة | العجلة تظهر في مكان خاطئ أو مشوّهة | تفعيل Per-Monitor DPI v2 في manifest التطبيق |
| مضادات الفيروسات | الـ Hooks العامة قد تثير الشك | توقيع الكود رقمياً، وشرح الصلاحيات بوضوح في أول تشغيل |
| تحذير SmartScreen | البرامج غير الموقعة تظهر بتحذير أزرق | في البداية مقبول؛ لاحقاً توقيع رقمي أو النشر عبر Microsoft Store |
| قائمة الزر الأيمن الجديدة في ويندوز 11 | الإضافات القديمة تختفي تحت «إظهار المزيد من الخيارات» | المرحلة 1: الطريقة الكلاسيكية عبر السجل؛ لاحقاً IExplorerCommand مع حزمة Sparse للظهور في القائمة الجديدة |
| OCR العربي | يعمل فقط إن كانت ميزة OCR العربية مثبتة، وقد يقلب ترتيب الكلمات | فحص `OcrEngine.IsLanguageSupported` وعرض زر تثبيت، وTesseract احتياطياً، واختبار ترتيب RTL مبكراً |
| حجم البرنامج | FFmpeg وLibreOffice والنماذج تضخّم المثبّت | المثبّت الأساسي يحمل FFmpeg فقط، والباقي يُحمَّل عند أول استخدام مع شريط تقدم |

## طرق التشغيل: الاختصار ونسخة الويب

البرنامج يعمل في الخلفية من شريط المهام، ويصل إليه المستخدم بستة طرق؛ الخمس الأولى من المرحلة 1.

1. اختصار على سطح المكتب وقائمة ابدأ يضعه المثبّت تلقائياً.
2. التشغيل مع بدء ويندوز (خيار في الإعدادات، عبر مفتاح Run في السجل).
3. أيقونة في شريط المهام بجانب الساعة: إيقاف مؤقت، فتح الرف، الإعدادات، السجل.
4. اختصار لوحة مفاتيح عام، مثل Ctrl+Alt+D لفتح الرف العائم أو تحويل محتوى الحافظة.
5. القائمة اليمنى في المستكشف وقائمة «إرسال إلى»: تعمل حتى لمن لا يحب السحب.
6. نسخة ويب PWA (المرحلة 4).

### نسخة الويب PWA

تطبيق ويب قابل للتثبيت يضع اختصاراً على سطح المكتب ويعمل داخل المتصفح دون رفع أي ملف؛ مناسب للأجهزة التي لا يُسمح فيها بتثبيت برامج. يمكن استضافته على نطاق فرعي مثل dropflow.scouthub.dev.

| الجزء | التقنية المجانية |
| --- | --- |
| الواجهة | Next.js أو Vite + React، بتصميم العجلة نفسه |
| الفيديو والصوت | ffmpeg.wasm |
| الصور | wasm-vips أو Canvas API |
| PDF | pdf-lib للدمج والتقسيم، وpdf.js للعرض والتحويل لصور |
| OCR | Tesseract.js مع العربية |
| التفريغ | whisper.cpp المترجم إلى WebAssembly (للملفات القصيرة) |
| العمل دون إنترنت | Service Worker + Manifest |

حدود نسخة الويب: لا تلتقط السحب في كل النظام، بل فقط داخل نافذة المتصفح؛ وهي أبطأ في الملفات الكبيرة. لذلك هي مكمّلة لبرنامج سطح المكتب لا بديل عنه، وتصلح أيضاً كصفحة تعريفية تجريبية للمنتج.

## خارطة الطريق

خمس مراحل بعد تجربة تقنية أولى، بإجمالي تقريبي 14 إلى 17 أسبوعاً لمطوّر واحد يعمل مع Claude Code. المدد تقديرية، وكل مرحلة لها شرط عبور قبل البدء بالتالية.

| المرحلة | المدة التقريبية | ما يُسلَّم | شرط العبور |
| --- | --- | --- | --- |
| 0. تجربة تقنية (Spike) | أسبوع | تطبيق صغير: Hook يلتقط السحب + Shift، نافذة شفافة تحت المؤشر، تستقبل الملف المُفلت وتطبع مساره | يعمل على ويندوز 10 و11، وعلى شاشتين بدقة مختلفة، ومن المستكشف وسطح المكتب والمتصفح |
| 1. النواة MVP | 4 أسابيع | العجلة الكاملة بالحركة، تحويل الصور والصوت والفيديو، عجلة الإجراءات، المعالجة الجماعية، أيقونة شريط المهام، الإعدادات، العربية/الإنجليزية، المثبّت والاختصار | 20 مستخدماً تجريبياً يحوّلون ملفاتهم دون أخطاء حرجة |
| 2. الأدوات والرف | 4 أسابيع | عجلة الأدوات، أدوات PDF، المستندات، الأرشيف، الرف العائم، تخصيص العجلة، أدوات المطورين، المعاينة، السجل والتراجع | كل أداة لها اختبار آلي على ملفات عيّنة |
| 3. الذكاء المحلي | 3 أسابيع | OCR عربي، تفريغ Whisper إلى SRT، إزالة الخلفية، مدير تحميل النماذج | دقة مقبولة على عيّنات عربية حقيقية، والنماذج لا تُحمَّل إلا بطلب المستخدم |
| 4. الأتمتة ونسخة الويب | 3 إلى 4 أسابيع | الوصفات المتسلسلة، المجلدات المراقبة، الحافظة، أداة سطر الأوامر، الإرسال للهاتف، نسخة PWA | الوصفات تعمل دون واجهة، ونسخة الويب تعمل دون إنترنت |
| 5. الإطلاق | أسبوع إلى أسبوعين | توقيع الكود، صفحة تعريفية، فيديو تعريفي، النشر على GitHub Releases وربما Microsoft Store | تثبيت نظيف على جهاز جديد دون تحذيرات حرجة |

لا تنتقل للمرحلة 1 إن فشلت التجربة التقنية على أحد النظامين؛ هذا هو الخطر الأكبر في المشروع كله.

## هيكل المستودع وملف CLAUDE.md

مستودع واحد بعدة مشاريع؛ كل مشروع له مسؤولية واحدة، حتى يعمل Claude Code على جزء دون أن يكسر جزءاً آخر.

```
DropFlow/
├─ CLAUDE.md                 ← تعليمات المشروع لـ Claude Code
├─ docs/                     ← هذه الخطة + قرارات التصميم (ADR)
├─ src/
│  ├─ DropFlow.App/          ← WPF: الأيقونة، الإعدادات، الإقلاع
│  ├─ DropFlow.Hooks/        ← Mouse/Keyboard Hooks + كشف السحب
│  ├─ DropFlow.Overlay/      ← العجلة الشعاعية والرف العائم
│  ├─ DropFlow.Core/         ← IConverter، سجل الأدوات، الوصفات، طابور المهام
│  ├─ DropFlow.Engines/      ← FFmpeg، Magick، QPDF، PDFium، LibreOffice، 7-Zip
│  ├─ DropFlow.AI/           ← OCR، Whisper، إزالة الخلفية، مدير النماذج
│  ├─ DropFlow.Shell/        ← القائمة اليمنى و«إرسال إلى»
│  └─ DropFlow.Cli/          ← أداة سطر الأوامر
├─ web/                      ← نسخة PWA
├─ tests/                    ← اختبارات + ملفات عيّنة لكل صيغة
├─ tools/                    ← سكربتات تحميل FFmpeg والمحركات
└─ installer/                ← Velopack أو Inno Setup
```

محتوى CLAUDE.md المقترح (يُكتب بالإنجليزية لأن Claude Code يتعامل معه كتعليمات عمل):

```markdown
# DropFlow
Windows 10/11 drag-and-convert utility. C# .NET 10, WPF + WPF-UI, MVVM (CommunityToolkit.Mvvm).

## Rules
- Every tool implements IConverter in DropFlow.Core; engines never referenced from UI.
- External engines run as child processes with timeouts and cancellation; never block the UI thread.
- Hooks run on a dedicated thread and only post events; no work inside the hook callback.
- Outputs never overwrite originals; write next to source with a suffix.
- All user-facing strings in Resources (ar + en); UI must work in RTL.
- Only MIT/Apache/BSD/LGPL libraries in-process. Ask before adding any dependency.
- Every new tool needs a test using a sample file from tests/samples.

## Commands
- Build: dotnet build
- Test: dotnet test
- Run: dotnet run --project src/DropFlow.App
```

## Skills وplugins ومشاريع جاهزة من GitHub

أهم ثلاثة للبدء: Superpowers للتخطيط والاختبار، وwin-dev-skills من مايكروسوفت للتغليف والمراجعة، وwpf-best-practices لنمط WPF. لا تثبّت كل شيء؛ كل skill إضافي يستهلك من سياق Claude.

### Skills وplugins لـ Claude Code

| الاسم | ماذا يفيد في DropFlow | التثبيت |
| --- | --- | --- |
| [obra/superpowers](https://trevorlasn.com/blog/superpowers-claude-code-skills) | عصف ذهني ثم خطة مفصّلة ثم تنفيذ على دفعات، مع TDD وتصحيح منهجي وتحقق قبل إعلان الإنجاز | `/plugin marketplace add obra/superpowers-marketplace` ثم `/plugin install superpowers@superpowers-marketplace` |
| [microsoft/win-dev-skills](https://github.com/microsoft/win-dev-skills) | من مايكروسوفت: إعداد بيئة ‎.NET 10، مراجعة كود، اختبار واجهات، وتغليف MSIX وتوقيع. مبني لـ WinUI 3 لكن التغليف والمراجعة مفيدان | `claude plugin marketplace add microsoft/win-dev-skills` ثم `claude plugin install winui@win-dev-skills` |
| [jcurbelo/skills: wpf-best-practices](https://claudemarketplaces.com/skills/jcurbelo/skills/wpf-best-practices) | قواعد WPF وMVVM وحقن التبعيات وasync | `npx -y skills add jcurbelo/skills --skill wpf-best-practices --agent claude-code` |
| [managedcode/dotnet-skills](https://skillselion.com/skills/managedcode/dotnet-skills/winui) | مجموعة skills لـ ‎.NET، منها winui ومقارنة WinUI/WPF | `npx skills add https://github.com/managedcode/dotnet-skills --skill winui` |
| [christian289/dotnet-with-claudecode](https://skillselion.com/plugin/christian289/dotnet-with-claudecode) | plugin لـ WPF وXAML وMVVM (شروحه بالكورية) | من مستودعه كـ plugin |
| [anthropics/skills](https://designrevision.com/blog/awesome-claude-code-skills) | الرسمية من Anthropic: skill-creator لصنع skill خاص بالمشروع، وfrontend-design لصفحة الموقع ونسخة PWA | `/plugin marketplace add anthropics/skills` |

أدلة لاكتشاف المزيد: [hesreallyhim/awesome-claude-code](https://agentskill.work/en/skills/hesreallyhim/awesome-claude-code) و[travisvn/awesome-claude-skills](https://gittrend.io/repo/travisvn/awesome-claude-skills) و[ComposioHQ/awesome-claude-skills](https://designrevision.com/blog/awesome-claude-code-skills).

اقتراح: بعد المرحلة 1 اطلب من Claude Code عبر skill-creator أن يصنع skill اسمه `dropflow-add-tool` يشرح خطوات إضافة أداة جديدة (واجهة IConverter، ملف عيّنة، اختبار، ترجمة النصوص، تسجيلها في العجلة). هذا يجعل إضافة عشرات الأدوات لاحقاً سريعة ومتسقة.

### مشاريع مفتوحة المصدر نتعلم منها

| المشروع | ما نأخذه منه | الترخيص |
| --- | --- | --- |
| Microsoft PowerToys | أمثلة جاهزة لـ Hooks الفأرة، والقائمة اليمنى الحديثة في ويندوز 11، وأداة تغيير حجم الصور | MIT: يمكن الاقتباس من كوده |
| [File Converter (Tichau)](https://thefilibusterblog.com/discover-the-versatile-open-source-file-converter-that-supports-many-formats/) | محوّل ويندوز من القائمة اليمنى بـ FFmpeg وImageMagick وGhostscript، وفكرة الإعدادات المسبقة (Presets) | GPL v3: للتعلّم فقط |
| [DropPoint](https://raw.githubusercontent.com/GameGodS3/DropPoint/main/README.md) | فكرة الرف العائم للسحب والإفلات، يدعم ويندوز 10 | Electron؛ مرجع للتجربة لا للكود |
| ShareX | التقاط النص OCR ومنتقي الألوان | GPL: للتعلّم فقط |

## خطة العمل مع Claude Code

اعمل بجلسة مستقلة لكل مرحلة: ابدأ بوضع التخطيط (Plan Mode)، راجع الخطة، ثم نفّذ. احفظ هذه الوثيقة داخل `docs/plan.md` في المستودع ليقرأها Claude في كل جلسة.

أوامر جاهزة تنسخها كما هي (بالإنجليزية لدقة التنفيذ):

**المرحلة 0 — التجربة التقنية**

```
Read docs/plan.md. Build a minimal spike in src/DropFlow.Hooks and a WPF test app:
1) low-level mouse hook on its own thread detects a drag (left down + move > SM_CXDRAG/SM_CYDRAG),
2) low-level keyboard hook detects a Shift tap during that drag,
3) show a transparent, topmost, borderless, per-monitor-DPI-aware window centered on the cursor,
4) that window is a drop target and logs the dropped file paths.
Add a test checklist for Windows 10 and 11, multi-monitor, Explorer, desktop and browser downloads.
```

**المرحلة 1 — النواة**

```
Using the spike, implement the radial wheel UI in DropFlow.Overlay (wedges, hover highlight, arrow keys, Enter, Esc,
open/close animation, circular batch progress). Define IConverter and a ToolRegistry in DropFlow.Core.
Implement image conversions with Magick.NET and audio/video with ffmpeg.exe as a child process.
Add tray icon, settings window (WPF-UI), Arabic + English resources with RTL, and a Velopack installer
that creates Desktop + Start Menu shortcuts and an optional run-at-startup setting. Write tests first.
```

**المرحلة 2 — الأدوات والرف**

```
Add the Tools wheel and Actions wheel. Implement PDF tools with QPDF + PDFiumCore, Office conversions with
LibreOffice headless (downloaded on first use), archives with SharpCompress/7za. Build the floating shelf docked
to a screen edge. Add per-file-type wheel customization stored as JSON. Add history with undo.
```

**المرحلة 3 — الذكاء المحلي**

```
Add DropFlow.AI: Windows.Media.Ocr with Arabic (check IsLanguageSupported, offer install), Tesseract fallback;
Whisper.net transcription to SRT/TXT; background removal with ONNX Runtime + an Apache/MIT model.
Add a model manager that downloads models only on user request, with size shown and resumable downloads.
```

نصائح للعمل:

- اطلب من Claude Code تنفيذ أداة واحدة في كل مرة مع اختبارها، لا عشر أدوات دفعة واحدة.
- بعد كل مرحلة: اطلب مراجعة كود (skill المراجعة من win-dev-skills أو superpowers) قبل الدمج.
- جرّب كل مرحلة يدوياً على جهاز ويندوز 10 حقيقي أو آلة افتراضية، لأن معظم المطورين يختبرون على 11 فقط.

## المخاطر والأسئلة المفتوحة

| الخطر | الاحتمال | الأثر | التخفيف |
| --- | --- | --- | --- |
| التقاط السحب غير مستقر في بعض التطبيقات | متوسط | عالٍ | التجربة التقنية أولاً + طرق تشغيل بديلة (Enter، القائمة اليمنى، الرف) |
| مضاد فيروسات يحجب البرنامج بسبب الـ Hooks | متوسط | عالٍ | توقيع رقمي، كود مفتوح أو شفاف، إرسال البرنامج لشركات الحماية للفحص |
| جودة PDF ← Word ضعيفة مقارنة بالأدوات المدفوعة | عالٍ | متوسط | وصفها بصدق «قد يتغير التنسيق» كما يفعل Tangerine |
| حجم التحميل كبير | متوسط | متوسط | تحميل المحركات والنماذج عند الطلب |
| OCR العربي يقلب ترتيب الكلمات | متوسط | متوسط | اختبار مبكر + معالجة RTL + Tesseract احتياطياً |
| مخالفة تراخيص عند البيع | منخفض إن التُزم بالجدول | عالٍ | مراجعة كل تبعية جديدة قبل إضافتها (قاعدة في CLAUDE.md) |
| استخدام اسم أو تصميم Tangerine | منخفض | عالٍ | نأخذ الفكرة لا الهوية: اسم وشعار وألوان وعجلة بتصميم خاص بـ DropFlow |

أسئلة تحتاج قرارك قبل المرحلة 1:

- [ ] هل DropFlow مجاني ومفتوح المصدر، أم مجاني بنسخة مدفوعة لاحقاً؟ (يحدد قيود التراخيص)
- [ ] المفاتيح الافتراضية: Shift / Alt / Ctrl مثل Tangerine، أم مفتاح واحد مع تبديل العجلات بالأسهم؟
- [ ] لغة الواجهة الافتراضية: العربية أم حسب لغة النظام؟
- [ ] النشر: GitHub Releases فقط، أم Microsoft Store أيضاً؟
- [ ] هوية بصرية: لون وشعار DropFlow قبل بناء العجلة النهائية.

## المصادر

- [Tangerine — موقع المنتج](https://tangerineformac.com/) والنص المرفق منه
- [Tangerine: Offline file modifier for Mac — chatgate.ai](https://chatgate.ai/post/tangerine)
- [Tangerine — MacGeneration](https://www.macg.co/logiciels/2026/10/tangerine-un-convertisseur-de-fichiers-toujours-portee-de-main-sur-mac-311381)
- [microsoft/win-dev-skills — GitHub](https://github.com/microsoft/win-dev-skills)
- [Introducing WinUI agent plugin — Microsoft DevBlogs](https://devblogs.microsoft.com/ifdef-windows/?p=1148)
- [Superpowers for Claude Code — Trevor Lasn](https://trevorlasn.com/blog/superpowers-claude-code-skills)
- [Awesome Claude Code Skills — DesignRevision](https://designrevision.com/blog/awesome-claude-code-skills)
- [wpf-best-practices skill — Claude Marketplaces](https://claudemarketplaces.com/skills/jcurbelo/skills/wpf-best-practices)
- [managedcode/dotnet-skills winui — Skillselion](https://skillselion.com/skills/managedcode/dotnet-skills/winui)
- [christian289/dotnet-with-claudecode — Skillselion](https://skillselion.com/plugin/christian289/dotnet-with-claudecode)
- [File Converter (Tichau) — overview](https://thefilibusterblog.com/discover-the-versatile-open-source-file-converter-that-supports-many-formats/)
- [DropPoint README](https://raw.githubusercontent.com/GameGodS3/DropPoint/main/README.md)
- [Arabic OCR in Windows — Microsoft Q&A](https://learn.microsoft.com/en-us/answers/a/11577099)

ملاحظة: تراخيص المكتبات في جدول التقنيات من المعرفة العامة؛ تحقّق من صفحة كل مكتبة قبل اعتمادها نهائياً.
