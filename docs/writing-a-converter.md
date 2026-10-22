# Writing a converter

A **converter** implements `IConverter` (`src/DropFlow.Core/Operations/Operations.cs`) for one or more **operations**.
An operation id is either `convert:<ext>` (e.g. `convert:png`) or a **tool id from `catalog/tools.json`** (e.g. `img.compress`).
Every tool id you implement must already exist in the catalog; every `convert:<ext>` must be one of its category's `outputs`.

## Contract
```csharp
public sealed class MyConverter : IConverter
{
    public IReadOnlyCollection<string> Operations => ["convert:png", "img.compress"];

    // True only for operation+input extension pairs you REALLY handle (ext has no dot, lower-case).
    // Never return true for converting a format to itself.
    public bool Supports(string operation, string inputExtension) => ...;

    public EngineId EngineFor(string operation) => EngineId.Ffmpeg;      // None for pure in-process work
    public IReadOnlyList<ToolOption> OptionsFor(string operation) => []; // non-empty => the UI asks first

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken ct) { ... }
}
```
`ConversionJob` has `Inputs`, `Operation`, `Options` (read with `GetString/GetInt/GetDouble/GetBool`) and `OutputDirectory`.
The queue calls `RunAsync` once per input file, except for tools marked `"multi": true` in the catalog (merge, join, collage…),
which get all inputs in one job.

## Module entry point
Each engine project exposes exactly one static class the app calls:
```csharp
public static class PdfModule
{
    public static IReadOnlyList<IConverter> Create(IEngineProvider engines) => [ ... ];
}
```

## Rules (all enforced by review)
1. **Never overwrite or delete an input.** Name outputs with `new OutputPathResolver().ForFile / ForGroup / ForFolder`
   (suffix examples: `converted`, `compressed`, `cropped`, `merged`, `pages`). Honour `job.OutputDirectory`.
2. **Expected failures return `ConversionResult.Fail("clear message")`** (corrupt file, wrong password, engine error). Throw only for bugs.
   If `EngineFor` is not installed, fail with a message that names the engine; the app offers to install it.
3. **Honour `CancellationToken`**; report progress `0..1` when the work is long enough to matter. Clean up partial outputs and temp files on failure/cancel.
4. **External programs** go through `DropFlow.Engines.Common.ProcessRunner` (argument list, no shell strings, timeout, cancellation).
   Locate them with the injected `IEngineProvider.Locate(EngineId)`; never hard-code paths.
5. **No network access** and **no downloading of binaries or models** in converters or tests.
6. Only dependencies whose licence is MIT, BSD, Apache-2.0, MPL-2.0, ISC, Zlib or dynamically-linked LGPL (never GPL/AGPL in-process; see `CLAUDE.md` and ADR-0010). Add them with
   `dotnet add <project> package <name>`; list every added package (name, version, licence, why) in your final report.
7. Unicode paths and paths with spaces must work (Arabic file names are a core use case). Test with one.
8. All user-visible strings are resource keys, not literals; converters return messages for logs only (English is fine there).

## Notes the user sees
A successful run can carry a `ConversionResult.Note` ("already as small as it gets", "nothing to trim"). Converters stay free of UI text, so a
note meant for the user is a **resource key** starting with `note.`, optionally followed by `|`-separated arguments for `{0}`, `{1}`…:
build it with `NoteText.Key("note.img.targetNotReached", 412)` and add the key to `strings.en.json` and `strings.ar.json`. The app and the CLI
translate it with `NoteText.Localize`; any other note text is shown as it is. Media operations set `MediaContext.Note`.

## Tools that need an AI model
A converter that needs a downloadable model never downloads it. It checks `ModelManager.IsInstalled(id)` and, when the model is missing, returns
`ConversionResult.Fail(AiFailures.MissingModel(models, id))`. The app recognises that sentence (`AiFailures.TryParseMissingModel`), asks the user
(size, source, licence), downloads with SHA-256 verification and runs the job again; the CLI does the same only with `dropflow models download <id> --yes`.
A model made of several files (the translation models: encoder, decoder, vocabulary, tokenizer) is **one catalogue entry with `Files`**: one id, one
consent, one folder under the models folder (`ModelManager.FilePathOf(id, name)`), every file pinned by size and SHA-256. Pin Hugging Face URLs to a
commit (`/resolve/<40 hex>/`) and compute the hashes from a real download; `ModelCatalogTests` enforces the rest. The translation tools show the pattern:
`TextTranslateConverter`, `ImageTranslateConverter` and `PdfTranslateConverter` take an `ITextTranslator` (tests pass a fake, real runs use `MarianTextTranslator`).

## A tool for more than one category
A catalog tool belongs to one `cat`, but `"alsoIn": ["video"]` offers it on other categories' wheels too (`aud.trim-silence` trims the sound of
a video). The converter's `Supports` must then accept those inputs as well, and nothing else.

## Option formats filled by an editor
Some options are lists a future visual editor fills. They are plain `Text` options so the generic form, recipes and the CLI can set them too.
- `vid.redact` → `boxes`: compact text `x,y,w,h@start-end; …` (pixels of the picture as displayed; `@start-end` is optional, either side may be
  empty, times in seconds or mm:ss), or JSON `[{"x":100,"y":50,"w":300,"h":200,"start":5,"end":"0:12"}]` (`start`/`end` optional, number of
  seconds or a time string). At most 64 boxes. The single-box options `x`/`y`/`w`/`h`/`start`/`end` still work and add one more box.

## Tests (TDD)
- Put tests in `tests/<Project>.Tests`. Write the failing test first.
- Use `DropFlow.TestSupport`: `Samples.Png/Wav/Pdf/Srt` generate valid sample files in code; `TempDir` for scratch space.
- Pure-library work (PDFsharp, PdfPig, Magick.NET, SharpCompress…) gets **real end-to-end tests** on generated samples that open
  the produced file with a *different* reader than the one that wrote it.
- Work that needs an external program: unit-test the **argument building and output parsing** as pure functions, and add
  `[EngineFact(EngineId.X)]` integration tests (they are skipped, visibly, when the engine is absent).
- Cover: success, cancellation, corrupt input, name collisions (never overwrite), Unicode path, `Supports` truthfulness.
- After finishing, mutate one rule on purpose and confirm a test fails (then revert).

## Registering in the app
The app composes `Module.Create(engines)` results into one `ToolRegistry`; the wheel then shows exactly the operations that have
a converter, in catalog order. Nothing else needs wiring.

A tool may also get a **dedicated editor** (a timeline, boxes drawn on the picture) instead of the generic options form: implement
`IOptionsEditor` (`src/DropFlow.Core/Runner/OperationRunner.cs`) in the app and add it to the `CompositeOptionsEditor` in `AppHost`.
The editor only returns option values (and, for multi-file tools, a new order of the selected files); the job still runs through the
queue with the converter, so every value an editor produces must also be accepted from the CLI and recipes. The media editors live in
`src/DropFlow.App/Views/Editors/Media/` (`MediaEditors` lists which tools have one).
