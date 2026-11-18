// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using DropFlow.Core.Operations;

namespace DropFlow.Engines.Data.Tests;

internal static class Kit
{
    public static readonly IProgress<JobProgress> NoProgress = new Progress<JobProgress>();

    public static Task<ConversionResult> Run(IConverter converter, string operation, string input, string? outputDirectory = null, CancellationToken ct = default) =>
        converter.RunAsync(new ConversionJob([input], operation, null, outputDirectory), NoProgress, ct);

    public static async Task<string> Ok(Task<ConversionResult> run)
    {
        var result = await run;
        Assert.True(result.Success, result.Error);
        return Assert.Single(result.Outputs);
    }

    public static Task<string> Convert(string input, string target) => Ok(Run(new DataConverter(), "convert:" + target, input));

    public static string Read(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(path)).TrimStart((char)0xFEFF);

    /// <summary>The lines of a CSV/TSV file written by the converter (no quoted line breaks in these tests).</summary>
    public static string[] Lines(string path) => Read(path).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    public static string[] FilesIn(string folder) => Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToArray()!;
}
