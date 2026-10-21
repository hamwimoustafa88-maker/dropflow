## What and why · ماذا ولماذا

<!-- Describe the change and link the issue (e.g. "Closes #12"). Arabic or English is fine. · صف التغيير واربط الـ Issue. -->

## How it was tested · كيف اختُبر

<!-- Tests added, commands run, manual checks (Windows version, Arabic/RTL, English). · الاختبارات والأوامر والفحص اليدوي. -->

## Checklist · قائمة التحقق

- [ ] Every commit is signed off (`git commit -s`, DCO). · كل commit موقّع بـ DCO.
- [ ] Commit messages follow Conventional Commits (`feat(pdf): ...`, `fix: ...`).
- [ ] One focused change (one tool or one fix). · تغيير واحد مركّز.
- [ ] Test-first: a test fails without the change and passes with it; `dotnet test DropFlow.sln` (and `npm test` / `npm run lint` in `web/` if touched) pass. · الاختبارات تنجح.
- [ ] If `catalog/tools.json` changed: `node catalog/build.mjs` prints OK and the regenerated files are committed.
- [ ] Converters follow `docs/writing-a-converter.md`: originals are never overwritten or deleted; cancellation, corrupt input and an Arabic path are tested.
- [ ] No file contents, names or metadata are sent over the network; no network calls in converters, tests or the web app. · لا اتصال شبكي بمحتوى الملفات.
- [ ] Every new user-visible string exists in **Arabic and English**, and the UI works **right-to-left**. · النصوص بلغتين والواجهة تعمل RTL.
- [ ] No new dependency — or it was agreed in an issue first and added to `THIRD-PARTY-NOTICES.md` (no AGPL, proprietary or non-commercial-only components). · لا مكتبة جديدة دون نقاش مسبق.
- [ ] No binaries committed (engines, models, installers).
- [ ] `docs/status.md` and other docs updated if behaviour changed (verified vs. unverified stated honestly).
- [ ] For UI changes: screenshots in Arabic and English. · لقطات شاشة بالعربية والإنجليزية.
