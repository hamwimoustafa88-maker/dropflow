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
6. Only dependencies whose licence is MIT, BSD, Apache-2.0, LGPL, MPL-2.0 or GPL-compatible (see `CLAUDE.md`). Add them with
   `dotnet add <project> package <name>`; list every added package (name, version, licence, why) in your final report.
7. Unicode paths and paths with spaces must work (Arabic file names are a core use case). Test with one.
8. All user-visible strings are resource keys, not literals; converters return messages for logs only (English is fine there).

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
