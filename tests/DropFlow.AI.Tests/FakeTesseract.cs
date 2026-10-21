using DropFlow.Core.Operations;
using DropFlow.TestSupport;

namespace DropFlow.AI.Tests;

/// <summary>
/// A stand-in for tesseract.exe: a batch file that follows tesseract's command-line contract (<c>--list-langs</c>;
/// <c>IMAGE OUTBASE -l LANGS [--dpi N] tsv|pdf</c> writes OUTBASE.tsv / OUTBASE.pdf) and logs every call. It lets the process plumbing
/// (ProcessRunner, argument order, output files, progress lines, cleanup) be tested without Tesseract installed. It proves nothing
/// about recognition quality; the real-Tesseract tests are [EngineFact] and skipped here.
/// </summary>
public sealed class FakeTesseract
{
    public string ScriptPath { get; }
    public string LogPath { get; }
    public string CannedTsvPath { get; }
    public string CannedPdfPath { get; }

    public FakeTesseract(TempDir dir, string tsv = "", bool failPdf = false, bool pdfIsGarbage = false, params string[] languages)
    {
        var home = dir.Combine("fake-tesseract");
        Directory.CreateDirectory(home);
        ScriptPath = Path.Combine(home, "tesseract.cmd");
        LogPath = Path.Combine(home, "args.log");
        CannedTsvPath = Path.Combine(home, "canned.tsv");
        CannedPdfPath = Path.Combine(home, "canned.pdf");

        File.WriteAllText(CannedTsvPath, tsv);
        if (pdfIsGarbage) File.WriteAllText(CannedPdfPath, "this is not a pdf");
        else Samples.Pdf(CannedPdfPath, pages: 2);

        languages = languages.Length == 0 ? ["ara", "eng", "osd"] : languages;
        var langLines = string.Join(Environment.NewLine, languages.Select(l => $"echo {l}"));
        var script = $"""
            @echo off
            setlocal enabledelayedexpansion
            if "%~1"=="--list-langs" goto langs
            >>"%~dp0args.log" echo CD=%CD% ARGS=%*
            set "last="
            for %%a in (%*) do set "last=%%~a"
            if /I "!last!"=="tsv" (
              copy /y "%~dp0canned.tsv" "%~2.tsv" >nul
              exit /b 0
            )
            if /I "!last!"=="pdf" (
              {(failPdf ? "echo Error: cannot write pdf 1>&2 & exit /b 3" : "echo Page 1 : %~1 1>&2 & echo Page 2 : %~1 1>&2 & copy /y \"%~dp0canned.pdf\" \"%~2.pdf\" >nul & exit /b 0")}
            )
            echo unexpected arguments 1>&2
            exit /b 2
            :langs
            echo List of available languages in "C:\fake\tessdata/" ({languages.Length}):
            {langLines}
            exit /b 0
            """;
        File.WriteAllText(ScriptPath, script.ReplaceLineEndings("\r\n")); // batch files need CRLF
    }

    public FakeEngines Engines => new((EngineId.Tesseract, ScriptPath));

    public string[] Calls => File.Exists(LogPath) ? File.ReadAllLines(LogPath) : [];
}
