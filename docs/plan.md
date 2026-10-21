# DropFlow — الخطة الرئيسية

آخر تحديث: 2026-10-20 · النسخة الأولى القديمة محفوظة في [archive/first-plan.md](archive/first-plan.md)

DropFlow **نسخة مطوّرة من Tangerine** (tangerineformac.com) لويندوز والويب: تسحب ملفاً، تضغط مفتاحاً، فتظهر عجلة دائرية لتحويل الملف أو معالجته **محلياً بلا رفع**، مع إضافات لا يملكها الأصل. الخط المرجعي للتكافؤ: [reference/tangerine-features.md](reference/tangerine-features.md) (مقروء من الموقع الحي بتاريخ 2026-10-20). قائمة الأدوات الكاملة: [feature-catalog.md](feature-catalog.md) (مُولَّدة من `catalog/tools.json`).

## 1. القرارات المحسومة

| القرار | الاختيار |
|---|---|
| الترخيص | **مملوك، جميع الحقوق محفوظة — غير مفتوح المصدر** (قرار المالك 2026-10-21، [ADR-0010](adr/0010-proprietary-license-and-dependency-policy.md)؛ كان GPL في [ADR-0001](adr/0001-gpl3-license-and-dependency-policy.md)) |
| المنصات | تطبيق ويندوز 10/11 (C# .NET 10 + WPF + WPF-UI) **+** نسخة ويب تعالج داخل المتصفح (Next.js على Vercel) |
| مفاتيح السحب | Shift تحويل · Ctrl أدوات · Alt إجراءات — قابلة للتغيير ([ADR-0003](adr/0003-drag-keys-shift-ctrl-alt.md)) |
| الأولوية بعد النواة | PDF أولاً ثم الوسائط |
| الويب | معالجة داخل المتصفح (WASM)، لا رفع ملفات للخادم ([ADR-0004](adr/0004-web-in-browser-processing.md)) |
| صفحة الهبوط | تُبنى **مع المرحلة 1**، بلا تسجيل |
| التسجيل والدفع | **مؤجّلان للمرحلة 9** ([ADR-0005](adr/0005-accounts-payments-deferred.md)) |
| التوزيع | GitHub Releases + winget (Velopack للتحديث التلقائي)، Microsoft Store لاحقاً |

## 2. ما يميّز DropFlow عن Tangerine

ويندوز + ويب · واجهة عربية كاملة RTL · مجموعة PDF أشمل (تكافؤ iLovePDF) · DropFlow Encoder (H.264/H.265/AV1، GPU، حجم مستهدف، مسبقات منصات) · تحويل الكلام إلى نص بـ Whisper (عربي) · OCR عربي · إزالة الخلفية · رف عائم · وصفات ومجلدات مراقبة · CLI. الهدف الأدنى: لا فئة في DropFlow أقل من Tangerine (يتحقق منه `node catalog/build.mjs` آلياً).

**تكييفات ويندوز:** Pages/Keynote/Numbers مدخلات فقط · AirDrop ← لوحة المشاركة + إرسال للهاتف بـ QR · Terminal ← Windows Terminal/PowerShell · قصاصات Finder ← الحافظة · اللمس: ضغطة مطوّلة قبل السحب (يُختبر في Spike).

## 3. التقنيات والتراخيص

| المجال | الأداة | الترخيص | الربط |
|---|---|---|---|
| الفيديو والصوت | FFmpeg (نسخة GPL: x264, x265, SVT-AV1, lame, opus, libass) | GPL | عملية خارجية |
| الصور | Magick.NET | Apache-2.0 | NuGet |
| PDF: دمج/تقسيم/حماية/إصلاح | qpdf | Apache-2.0 | عملية خارجية |
| PDF: ضغط وPDF/A | Ghostscript | AGPL-3.0 | عملية خارجية فقط |
| PDF: عرض وتحويل لصور | PDFiumCore | BSD/Apache | NuGet |
| PDF: كتابة (علامة مائية، ترقيم، بيانات وصفية) | PDFsharp | MIT | NuGet |
| PDF: نص وجداول | PdfPig | Apache-2.0 | NuGet |
| Office ⇄ PDF/EPUB/ODF | LibreOffice headless (يُحمَّل عند الطلب) | MPL-2.0 | عملية خارجية |
| Word/Excel إنشاءً | DocumentFormat.OpenXml, ClosedXML | MIT | NuGet |
| HTML → PDF | WebView2 `PrintToPdfAsync` | MS Redistributable | مدمج في ويندوز 10/11 |
| Markdown وتحويلات نصية | Pandoc | GPL | عملية خارجية |
| الأرشيف | SharpCompress + 7-Zip | MIT / LGPL | NuGet + CLI |
| OCR | Windows.Media.Ocr + Tesseract (ara) | مدمج / Apache-2.0 | WinRT / NuGet |
| الكلام إلى نص | Whisper.net (whisper.cpp) | MIT | NuGet + نماذج عند الطلب |
| إزالة الخلفية/الكشف | ONNX Runtime + U²-Net/BiRefNet | MIT / Apache | NuGet + .onnx |
| QR | ZXing.Net | Apache-2.0 | NuGet |
| الواجهة | WPF-UI، CommunityToolkit.Mvvm | MIT | NuGet |
| التثبيت والتحديث | Velopack | MIT | أداة بناء |
| الويب | Next.js, Tailwind, next-intl, ffmpeg.wasm, pdf-lib, pdf.js, Tesseract.js, transformers.js | MIT/Apache | npm |

تحذير: نموذج RMBG-1.4 غير تجاري — ممنوع. أي تبعية جديدة تُسأل عنها قبل الإضافة وتُسجَّل في `THIRD-PARTY-NOTICES.md`.

## 4. تحديات ويندوز وحلولها (ملخّص)

| التحدي | الحل |
|---|---|
| لا حدث «بدأ السحب» في ويندوز | Mouse Hook (زر أيسر + حركة أكبر من `SM_CXDRAG`) ثم Keyboard Hook لنقرة المفتاح |
| معرفة الملف المسحوب | العجلة تظهر تحت المؤشر وتكون Drop Target؛ ويندوز يسلّمها قائمة الملفات |
| تعارض Shift/Ctrl/Alt مع المستكشف | «نقرة» على المفتاح ثم الإفلات على العجلة لا على المستكشف |
| مهلة الـ Hooks | خيط مستقل بحلقة رسائل؛ لا عمل داخل الـ callback |
| تطبيقات بصلاحية المسؤول | لا يعمل السحب منها؛ نوثّقه ولا نشغّل DropFlow كمسؤول |
| شاشات متعددة/DPI | Per-Monitor DPI v2 في manifest |
| مضادات الفيروسات وSmartScreen | كود مفتوح، توقيع رقمي لاحقاً، شرح الصلاحيات في أول تشغيل |
| قائمة الزر الأيمن في ويندوز 11 | كلاسيكية عبر السجل أولاً، ثم IExplorerCommand + Sparse package |
| OCR العربي | فحص `IsLanguageSupported`، Tesseract احتياطي، اختبار RTL مبكر |
| حجم المثبّت | الأساسي يحمل FFmpeg فقط؛ LibreOffice/النماذج تُحمَّل عند أول استخدام |

## 5. مجموعة PDF (تكافؤ iLovePDF)

| الأداة | ويندوز | ويب |
|---|---|---|
| Merge / Split / Rotate / Organize | qpdf + مصغّرات PDFium | pdf-lib + pdf.js |
| Compress (3 مستويات) | Ghostscript + qpdf | Ghostscript WASM |
| PDF → JPG/PNG | PDFiumCore (300 DPI) | pdf.js |
| JPG/PNG → PDF | PDFsharp + Magick.NET | pdf-lib |
| Word/PPT/Excel → PDF | LibreOffice؛ Office عبر COM إن وُجد | ويندوز فقط |
| HTML → PDF | WebView2 | ويندوز فقط |
| PDF → Word / PowerPoint / Excel | LibreOffice (v1) ← PdfPig + OpenXml/ClosedXML (v2، المرحلة 5) | ويندوز فقط |
| Protect AES-256 / Unlock (كلمة مرور معروفة فقط) | qpdf | qpdf WASM |
| Watermark / Page numbers / Metadata | PDFsharp | pdf-lib |
| Repair / PDF/A | qpdf → Ghostscript | Ghostscript WASM |
| Crop / Sign / Redact حقيقي / Compare | المرحلة 4 | لاحقاً |
| OCR (PDF قابل للبحث) | Tesseract + Windows OCR (المرحلة 5) | Tesseract.js |

عجلة PDF: **Shift** ← DOCX · PPTX · XLSX · JPG · PNG · EPUB · TXT · MD (+PDF/A) — **Ctrl** ← Compress · Merge · Split · Organize · Rotate · Protect/Unlock · Watermark · Metadata.

## 6. استوديو الوسائط

- **أدوات Tangerine للفيديو والصوت كلها** (انظر الكتالوج) عبر FFmpeg: trim/split بـ `-c copy` عند الإمكان، speed بـ `atempo`، visualize بـ `showwaves`، bleep بنطاقات زمنية، redact بـ `boxblur/drawbox`، snapshot بمستعرض إطارات.
- **DropFlow Encoder** (نافذة طابور): MP4/MKV/WebM/MOV · MP3/WAV/M4A/FLAC/Opus/OGG · x264/x265/SVT-AV1/VP9 + NVENC/QSV/AMF (كشف بترميز تجريبي) · مسبقات: «نفس الجودة أصغر»، واتساب، Discord ‏10MB، بريد 25MB، YouTube 1080p/4K، Reels/TikTok 9:16، **حجم مستهدف** بمرورين · loudnorm بمرورين · تقدم عبر `-progress pipe:1`، إيقاف/إلغاء/ETA · ffprobe لمعلومات الملف.
- **الكلام إلى نص:** Whisper.net + نماذج ggml عند الطلب (tiny→large-v3-turbo؛ الافتراضي turbo q5 للعربية)، CPU/CUDA/Vulkan، WAV ‏16kHz mono + VAD، مخرجات SRT/VTT/TXT/DOCX/JSON، كشف لغة أو فرض العربية، ترجمة للإنجليزية، علامات RTL صحيحة، وصفة «فرّغ واحرق الترجمة».

## 7. الويب: صفحة الهبوط + DropFlow Web

- **المكدس:** Next.js (App Router) + TypeScript + Tailwind + next-intl (ar/en، RTL) على Vercel؛ مجلد `web/`.
- **Landing (المرحلة 1):** Hero تفاعلي لمحاكاة العجلة، عدّادات من الكتالوج، تبويبات الفئات، جدول الصيغ، قسم الخصوصية، «تحميل لويندوز» + «جرّب أونلاين» + GitHub، عربي/إنجليزي. هوية DropFlow خاصة. **بلا تسجيل.**
- **DropFlow Web (المرحلة 6):** `/app` — اسحب ملفاً إلى الصفحة واضغط Shift/Ctrl فتظهر العجلة (حدث `dragover` يعطي حالة المفاتيح)؛ المعالجة في Web Workers بـ WASM؛ ffmpeg.wasm يتطلب ترويسات COOP/COEP؛ PWA تعمل دون إنترنت؛ الأدوات غير المتاحة في المتصفح (Office، PDF→Word) تُعلَّم «في تطبيق ويندوز».
- **كتالوج مشترك:** `catalog/tools.json` يقرؤه التطبيق والويب والـ Landing.

## 8. خارطة الطريق

| # | المرحلة | المدة | المحتوى | شرط العبور |
|---|---|---|---|---|
| 0 | Spike | 1 أسبوع | تثبيت .NET 10 SDK؛ Hooks + Shift tap + نافذة شفافة Drop Target؛ اختبار اللمس | Win10 + Win11 + شاشتان + المستكشف/سطح المكتب/المتصفح |
| 1 | النواة + Landing | 5 أسابيع | العجلة، تحويلات الصور/الصوت/الفيديو، Compress/Metadata/Crop للصور، الإجراءات، المعالجة الجماعية، Tray، الإعدادات، ar/en RTL، Velopack، **Landing منشورة** | 20 مستخدماً تجريبياً بلا أخطاء حرجة |
| 2 | PDF والمستندات | 5 أسابيع | القسم 5 + Documents/Slides/Spreadsheets/EPUB + Create PDF | اختبار لكل أداة على ملف عيّنة |
| 3 | استوديو الوسائط | 4 أسابيع | القسم 6 + Subtitles | Encoder يحقق الحجم المستهدف ±5%؛ تفريغ عربي مقبول |
| 4 | استوديو الصور والرف | 3 أسابيع | Edit/Annotate/Add BG/Collage/Redact، Float، الأرشيف، التخصيص، السجل والتراجع، أدوات المطورين، أدوات PDF المتقدمة | — |
| 5 | الذكاء المحلي | 2 أسبوع | OCR عربي، إزالة الخلفية، كشف وجوه/نص، PDF→Word v2 | النماذج تُحمَّل بطلب المستخدم فقط |
| 6 | DropFlow Web | 4 أسابيع | القسم 7 (التطبيق + PWA) | يعمل دون إنترنت؛ لا طلب شبكة يحمل محتوى ملف |
| 7 | الأتمتة | 2–3 أسابيع | وصفات، مجلدات مراقبة، الحافظة، CLI، إرسال للهاتف | الوصفات تعمل دون واجهة |
| 8 | الإطلاق | 1–2 أسبوع | توقيع، GitHub Releases + winget، Landing v2 + فيديو | تثبيت نظيف بلا تحذيرات حرجة |
| 9 | **مؤجّل** — الحسابات والدفع | — | تسجيل (Supabase Auth)، خطة مدفوعة عبر Merchant of Record، مزامنة الإعدادات والوصفات | يُخطَّط عند الوصول إليه |

لا تنتقل للمرحلة 1 إن فشل الـ Spike على أحد النظامين. ملاحظة للمرحلة 9: المشروع مملوك وغير مفتوح المصدر، فيمكن بيع التطبيق نفسه أو اشتراكاته دون قيد GPL.

## 9. المخاطر

| الخطر | التخفيف |
|---|---|
| التقاط السحب غير مستقر في بعض التطبيقات | Spike أولاً + طرق بديلة (Enter، القائمة اليمنى، الرف، السحب إلى نافذة DropFlow) |
| مضاد فيروسات يحجب الـ Hooks | كود مفتوح، توقيع، فحص لدى الشركات |
| جودة PDF→Word دون الأدوات المدفوعة | وصفها بصدق «قد يتغير التنسيق»، v2 لاحقاً |
| حجم التحميل | تحميل المحركات والنماذج عند الطلب |
| OCR عربي يقلب الترتيب | اختبار مبكر + Tesseract احتياطي |
| مخالفة تراخيص | قاعدة «اسأل قبل أي تبعية» + THIRD-PARTY-NOTICES |
| الاقتراب من هوية Tangerine | اسم وشعار وألوان ونصوص خاصة بـ DropFlow؛ نأخذ الفكرة لا الأصول |
| ffmpeg.wasm ثقيل/بطيء | الويب مكمّل لا بديل؛ حد حجم ملف وتنبيه، والتطبيق للملفات الكبيرة |

## 10. أسئلة مفتوحة (قبل المرحلة 1)

- [ ] لغة الواجهة الافتراضية: العربية أم حسب لغة النظام؟ (مقترح: حسب النظام)
- [ ] الهوية البصرية: اللون والشعار (تُصمَّم عبر ui-ux-pro-max قبل بناء العجلة النهائية والـ Landing)
- [ ] النطاق: dropflow.scouthub.dev أم نطاق مستقل؟

## 11. العمل مع Claude Code

- Plugins المفعّلة للمشروع: **superpowers** (brainstorming → writing-plans → TDD → verification) و**ui-ux-pro-max** (العجلة، الإعدادات، Landing، الويب). `pw` و`a11y-audit` من claude-skills تُفعَّل في المرحلة 6.
- جلسة لكل مرحلة، تبدأ بـ Plan Mode. أداة واحدة في كل مرة مع اختبارها. مراجعة كود بعد كل مرحلة. اختبار يدوي على Windows 10 حقيقي أو آلة افتراضية.
- بعد المرحلة 1: skill خاص `dropflow-add-tool` (ملف تعريف في الكتالوج، `IConverter`، عيّنة، اختبار، ترجمة، تسجيل).
- **CLAUDE.md** يُحدَّث مع كل تعديل (سجل التاريخ إلزامي).

## 12. ما تغيّر أثناء التنفيذ (انحرافات عن الخطة، 2026-10-21)

| الخطة | ما حدث | السبب |
|---|---|---|
| WPF-UI | سمة WPF خاصة بـ DropFlow (`WheelTheme` + `UiKit`) | تحكم كامل بالعجلة والاتجاه RTL وبلا تبعية |
| مشروع Engines واحد | مشاريع منفصلة: Images / Media / Pdf / Archive / Web / AI، لكل منها `XModule.Create` | عزل التراخيص والاختبارات وإمكانية العمل بالتوازي |
| Ghostscript لضغط PDF وPDF/A | حُذف: ليس في winget ورخصته AGPL. الضغط بـ qpdf + إعادة ترميز الصور + وضع «extreme» (تحويل الصفحات لصور). **PDF/A غير منفّذ** | الترخيص والتوفر |
| PDFsharp الأحدث | مثبّت عند **6.2.4** (MIT)؛ الإصدار 6.5.0 فما فوق AGPL-3.0 | سياسة التراخيص (ADR-0001) |
| HTML→PDF بـ WebView2 كل مرة | متصفح خفي واحد يُعاد استخدامه ويُغلق بعد 30 ثانية خمول؛ الشبكة محجوبة افتراضياً | إنشاء متصفح جديد بعد إغلاق السابق تأخّر 20–30 ثانية؛ والخصوصية |
| مشاركة الهاتف بـ HttpListener | نقلها إلى Kestrel (إطار .NET المشترك، بلا حزمة جديدة) | HttpListener يرفض الاستماع على الشبكة دون صلاحيات المدير |
| الأدوات التي تفتح نافذة (Encoder) | واجهة `IInteractiveTool` في النواة: المشغّل يتحقق من المحرك ثم يفتح النافذة بدل الطابور | لا تُنتج ملفاً بنفسها |
| النماذج تُحمَّل «بطلب المستخدم» | عند فشل أداة بسبب نموذج ناقص يسأل التطبيق (الاسم والحجم والمصدر والترخيص) ثم ينزّل بحجم/بصمة مثبّتة ويعيد التشغيل مرة واحدة | موافقة صريحة، ولا تحميل صامت |
| بوابة Spike اليدوية قبل المرحلة 1 | لم تُنفَّذ: طلب المالك المتابعة دون توقف. الخطر ما زال مفتوحاً | موثّق في `docs/status.md` |

الوضع الصادق لكل جزء (ما تحقق منه فعلاً وما لم يُجرَّب) في [docs/status.md](status.md).
