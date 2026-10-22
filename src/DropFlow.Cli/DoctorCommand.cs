// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DropFlow.AI.Models;
using DropFlow.Core.Operations;
using DropFlow.Engines.Common;
using DropFlow.Engines.Media.Encoding;

namespace DropFlow.Cli;

/// <summary>What the doctor needs from the machine, so the report is tested without the real programs.</summary>
internal interface IDoctorSystem
{
    /// <summary>Runs a program briefly and returns its stdout and stderr together, or null when it could not run or failed.</summary>
    Task<string?> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct);

    /// <summary>The product version stored in the file, without starting it.</summary>
    string? FileVersion(string path);

    /// <summary>The installed WebView2 Runtime version, or null.</summary>
    string? WebView2Version();
}

internal sealed record DoctorEngine(string Engine, string? Path, string? Version);

internal sealed record DoctorEncoder(string Name, bool Present);

internal sealed record DoctorModel(string Id, string Purpose, bool Installed);

internal sealed record DoctorReport(
    string Version,
    string Os,
    string OsArchitecture,
    string ProcessArchitecture,
    string Runtime,
    string DataFolder,
    IReadOnlyList<DoctorEngine> Engines,
    IReadOnlyList<DoctorEncoder>? Encoders,
    string ModelsFolder,
    IReadOnlyList<DoctorModel> Models,
    string? WebView2);

/// <summary>
/// <c>dropflow doctor [--json]</c>: one report to paste into a bug: DropFlow's version, Windows and the processor architecture, every helper
/// program DropFlow found (path and version), the FFmpeg encoders that decide speed and quality (x264, x265, SVT-AV1, NVENC, Quick Sync, AMF),
/// the AI models on disk and the WebView2 Runtime. It only reads: programs are asked for their version (LibreOffice is never started; its
/// version comes from the file), nothing is installed or downloaded, and no file names of the user are printed.
/// </summary>
internal static partial class DoctorCommand
{
    /// <summary>Encoders the Encoder window and the video tools care about, in the order they are shown.</summary>
    public static readonly IReadOnlyList<string> KeyEncoders =
    [
        "libx264", "libx265", "libsvtav1", "libvpx-vp9",
        "h264_nvenc", "hevc_nvenc", "av1_nvenc",
        "h264_qsv", "hevc_qsv", "av1_qsv",
        "h264_amf", "hevc_amf", "av1_amf",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> RunAsync(IReadOnlyList<string> args, IEngineProvider engines, ModelManager models, string version, string dataFolder, TextWriter stdout, CancellationToken ct)
    {
        var report = await GatherAsync(engines, models, version, dataFolder, new WindowsDoctorSystem(), ct).ConfigureAwait(false);
        await stdout.WriteLineAsync(args.Contains("--json") ? Json(report) : Format(report)).ConfigureAwait(false);
        return 0;
    }

    public static async Task<DoctorReport> GatherAsync(IEngineProvider engines, ModelManager models, string version, string dataFolder, IDoctorSystem system, CancellationToken ct)
    {
        var found = new List<DoctorEngine>();
        foreach (var engine in Enum.GetValues<EngineId>().Where(e => e != EngineId.None))
        {
            var path = engines.Locate(engine);
            found.Add(new DoctorEngine(engine.ToString(), path, path is null ? null : await VersionOfAsync(engine, path, system, ct).ConfigureAwait(false)));
        }

        IReadOnlyList<DoctorEncoder>? encoders = null;
        if (engines.Locate(EngineId.Ffmpeg) is { } ffmpeg && await system.RunAsync(ffmpeg, ["-hide_banner", "-encoders"], ct).ConfigureAwait(false) is { } list)
        {
            var parsed = FfmpegEncoders.Parse(list);
            encoders = KeyEncoders.Select(name => new DoctorEncoder(name, parsed.Has(name))).ToList();
        }

        return new DoctorReport(
            version,
            RuntimeInformation.OSDescription.Trim(),
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            dataFolder,
            found,
            encoders,
            models.Directory,
            models.Catalog.Select(m => new DoctorModel(m.Id, m.Purpose.ToString(), models.IsInstalled(m.Id))).ToList(),
            system.WebView2Version());
    }

    private static async Task<string?> VersionOfAsync(EngineId engine, string path, IDoctorSystem system, CancellationToken ct)
    {
        // LibreOffice is a GUI program on Windows (soffice --version may open a window), and 7-Zip prints its banner for any command: read the file.
        if (engine is EngineId.LibreOffice) return system.FileVersion(path);
        string[] args = engine switch
        {
            EngineId.Ffmpeg or EngineId.Ffprobe => ["-hide_banner", "-version"],
            EngineId.SevenZip => ["i"],
            _ => ["--version"],
        };
        var output = await system.RunAsync(path, args, ct).ConfigureAwait(false);
        return (output is null ? null : ParseVersion(engine, output)) ?? system.FileVersion(path);
    }

    /// <summary>The version in a program's own version banner, or null when the text does not look like one.</summary>
    public static string? ParseVersion(EngineId engine, string output)
    {
        var match = engine switch
        {
            EngineId.Ffmpeg or EngineId.Ffprobe => FfmpegVersion().Match(output),
            EngineId.Qpdf => QpdfVersion().Match(output),
            EngineId.Tesseract => TesseractVersion().Match(output),
            EngineId.SevenZip => SevenZipVersion().Match(output),
            EngineId.Pandoc => PandocVersion().Match(output),
            _ => GenericVersion().Match(output),
        };
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string Format(DoctorReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"DropFlow {r.Version} (command line)");
        sb.AppendLine($"System:   {r.Os}, {r.OsArchitecture} (this process: {r.ProcessArchitecture}), {r.Runtime}");
        sb.AppendLine($"Data:     {r.DataFolder}");
        sb.AppendLine();
        sb.AppendLine("Helper programs:");
        foreach (var e in r.Engines)
            sb.AppendLine(e.Path is null ? $"  {e.Engine,-12} not found" : $"  {e.Engine,-12} {e.Version ?? "version unknown",-40} {e.Path}");
        sb.AppendLine();
        if (r.Encoders is null)
        {
            sb.AppendLine("FFmpeg encoders: FFmpeg not found");
        }
        else
        {
            sb.AppendLine("FFmpeg encoders (a GPU encoder listed here still needs a matching graphics card and driver):");
            foreach (var group in r.Encoders.Chunk(4))
                sb.AppendLine("  " + string.Join("   ", group.Select(e => $"{e.Name} {(e.Present ? "yes" : "no")}")));
        }
        sb.AppendLine();
        sb.AppendLine($"Models ({r.ModelsFolder}):");
        foreach (var m in r.Models)
            sb.AppendLine($"  {m.Id,-28} {m.Purpose,-18} {(m.Installed ? "installed" : "not downloaded")}");
        sb.AppendLine();
        sb.Append(r.WebView2 is null ? "WebView2: not installed (HTML, Markdown and Office files to PDF need it)" : $"WebView2: {r.WebView2}");
        return sb.ToString();
    }

    public static string Json(DoctorReport r) => JsonSerializer.Serialize(r, JsonOptions);

    [GeneratedRegex(@"ff(?:mpeg|probe) version (\S+)")]
    private static partial Regex FfmpegVersion();

    [GeneratedRegex(@"qpdf version (\S+)")]
    private static partial Regex QpdfVersion();

    [GeneratedRegex(@"tesseract v?(\d[\w.\-]*)")]
    private static partial Regex TesseractVersion();

    [GeneratedRegex(@"7-Zip (?:\(\w\) )?(\d+\.\d+)")]
    private static partial Regex SevenZipVersion();

    [GeneratedRegex(@"pandoc(?:\.exe)? (\d[\w.\-]*)")]
    private static partial Regex PandocVersion();

    [GeneratedRegex(@"(\d+\.\d+(?:\.\d+)*)")]
    private static partial Regex GenericVersion();
}

/// <summary>The real machine: short-lived processes with a timeout, file version resources, and the WebView2 registration in the registry.</summary>
internal sealed class WindowsDoctorSystem : IDoctorSystem
{
    /// <summary>The WebView2 Runtime's client id in EdgeUpdate (documented by Microsoft for detecting the Evergreen runtime).</summary>
    private const string WebView2ClientKey = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    public async Task<string?> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(executable, arguments, new ProcessOptions { Timeout = TimeSpan.FromSeconds(15) }, ct).ConfigureAwait(false);
            return result.Success ? result.StdOut + "\n" + result.StdErr : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public string? FileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var version = info.ProductVersion ?? info.FileVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public string? WebView2Version()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var (root, prefix) in new[]
        {
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\"),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\"),
            (Microsoft.Win32.Registry.CurrentUser, @"Software\"),
        })
        {
            using var key = root.OpenSubKey(prefix + WebView2ClientKey);
            if (key?.GetValue("pv") is string pv && pv.Length > 0 && pv != "0.0.0.0") return pv;
        }
        return null;
    }
}
