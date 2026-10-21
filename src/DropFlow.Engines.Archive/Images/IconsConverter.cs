using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using DropFlow.Core.Operations;
using DropFlow.Core.Output;
using DropFlow.Engines.Archive.Archives;
using ImageMagick;

namespace DropFlow.Engines.Archive.Images;

/// <summary>Writes a Windows icon (.ico) holding PNG-compressed images, the format Windows Vista and later (and every browser) read.</summary>
public static class IcoWriter
{
    /// <summary>The sizes in a standard multi-resolution icon.</summary>
    public static readonly IReadOnlyList<int> StandardSizes = [16, 24, 32, 48, 64, 128, 256];

    /// <param name="images">Square PNG images, each with its pixel size (1..256).</param>
    public static byte[] Write(IReadOnlyList<(int Size, byte[] Png)> images)
    {
        if (images.Count == 0 || images.Count > ushort.MaxValue) throw new ArgumentException("An icon needs between 1 and 65535 images.", nameof(images));

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);                    // reserved
        writer.Write((ushort)1);                    // type: icon
        writer.Write((ushort)images.Count);

        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            if (size is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(images), "Icon images are at most 256 pixels.");
            writer.Write((byte)(size == 256 ? 0 : size));   // width: 0 means 256
            writer.Write((byte)(size == 256 ? 0 : size));   // height
            writer.Write((byte)0);                          // palette colours (none)
            writer.Write((byte)0);                          // reserved
            writer.Write((ushort)1);                        // colour planes
            writer.Write((ushort)32);                       // bits per pixel
            writer.Write((uint)png.Length);
            writer.Write((uint)offset);
            offset += png.Length;
        }

        foreach (var (_, png) in images) writer.Write(png);
        writer.Flush();
        return stream.ToArray();
    }
}

/// <summary>
/// <c>img.icons</c>: from one picture makes
/// <list type="bullet">
/// <item><c>name (icons).ico</c> beside the source: a multi-size icon with 16, 24, 32, 48, 64, 128 and 256 pixel PNG-compressed images;</item>
/// <item>the folder <c>name (favicons)</c> with favicon.ico, favicon-16x16.png, favicon-32x32.png, favicon-48x48.png,
/// apple-touch-icon.png (180), android-chrome-192x192.png, android-chrome-512x512.png, site.webmanifest and favicons.html (the
/// &lt;link&gt; tags to paste into &lt;head&gt;).</item>
/// </list>
/// Non-square pictures are fitted inside the square on a transparent background (<c>fit=contain</c>, default) or centre-cropped
/// to fill it (<c>fit=cover</c>). Optional keys: <c>fit</c>, <c>appName</c> (manifest name, default the file name),
/// <c>themeColor</c> and <c>backgroundColor</c> (#rrggbb, default #ffffff).
/// </summary>
public sealed class IconsConverter : IConverter
{
    public const string OperationId = "img.icons";

    public static readonly IReadOnlyList<(string File, int Size)> FaviconPngs =
    [
        ("favicon-16x16.png", 16),
        ("favicon-32x32.png", 32),
        ("favicon-48x48.png", 48),
        ("apple-touch-icon.png", 180),
        ("android-chrome-192x192.png", 192),
        ("android-chrome-512x512.png", 512),
    ];

    private static readonly string[] ImageExtensions = ["jpg", "png", "webp", "heic", "tiff", "avif", "bmp", "gif", "svg", "ico"];
    private static readonly Regex HexColor = new("^#[0-9a-fA-F]{3,8}$", RegexOptions.Compiled);

    public IReadOnlyCollection<string> Operations { get; } = [OperationId];

    public bool Supports(string operation, string inputExtension) =>
        operation == OperationId && ImageExtensions.Contains(inputExtension.TrimStart('.').ToLowerInvariant());

    public EngineId EngineFor(string operation) => EngineId.None;
    public IReadOnlyList<ToolOption> OptionsFor(string operation) => []; // runs immediately; the optional keys above refine it

    public Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Run(job, progress, cancellationToken), cancellationToken);
    }

    private static ConversionResult Run(ConversionJob job, IProgress<JobProgress> progress, CancellationToken ct)
    {
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file.");
        var input = job.Inputs[0];
        if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {input}");

        var sw = Stopwatch.StartNew();
        using var created = new CreatedOutputs();
        try
        {
            var cover = string.Equals(job.GetString("fit"), "cover", StringComparison.OrdinalIgnoreCase);
            using var source = new MagickImage(input);
            source.AutoOrient();

            // Render every size once; the .ico and the favicon PNGs share them where the sizes match.
            var sizes = IcoWriter.StandardSizes.Concat(FaviconPngs.Select(f => f.Size)).Distinct().ToList();
            var rendered = new Dictionary<int, byte[]>();
            for (var i = 0; i < sizes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                rendered[sizes[i]] = RenderSquare(source, sizes[i], cover);
                progress.Report(new JobProgress(0.7 * (i + 1) / sizes.Count));
            }

            var ico = IcoWriter.Write(IcoWriter.StandardSizes.Select(s => (s, rendered[s])).ToList());
            var resolver = new OutputPathResolver();

            // 1) the multi-size .ico beside the source
            var icoPath = resolver.ForFile(input, "ico", "icons", job.OutputDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
            WriteNew(icoPath, ico);
            created.AddFile(icoPath);

            // 2) the favicons folder
            var folder = resolver.ForFolder(input, "favicons", job.OutputDirectory);
            Directory.CreateDirectory(folder);
            created.AddFolder(folder);

            WriteNew(Path.Combine(folder, "favicon.ico"), ico);
            foreach (var (file, size) in FaviconPngs)
                WriteNew(Path.Combine(folder, file), rendered[size]);

            var appName = job.GetString("appName");
            if (string.IsNullOrWhiteSpace(appName)) appName = Path.GetFileNameWithoutExtension(input);
            var theme = Color(job.GetString("themeColor"));
            var background = Color(job.GetString("backgroundColor"));
            WriteNew(Path.Combine(folder, "site.webmanifest"), Encoding.UTF8.GetBytes(BuildManifest(appName, theme, background)));
            WriteNew(Path.Combine(folder, "favicons.html"), Encoding.UTF8.GetBytes(BuildHtmlSnippet(theme)));

            created.Commit();
            progress.Report(new JobProgress(1));
            return ConversionResult.Ok([icoPath, folder], sw.Elapsed);
        }
        catch (MagickException ex)
        {
            return ConversionResult.Fail($"The image could not be read: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConversionResult.Fail($"Could not write the icons: {ex.Message}");
        }
    }

    /// <summary>Scales the picture to a size x size PNG with transparency, never stretching it.</summary>
    internal static byte[] RenderSquare(MagickImage source, int size, bool cover)
    {
        using var image = (MagickImage)source.Clone();
        image.Alpha(AlphaAction.Set); // JPEG has no transparency channel: add one so the padding can be transparent
        image.BackgroundColor = MagickColors.Transparent;

        var target = (uint)size;
        if (cover)
        {
            image.Resize(new MagickGeometry(target, target) { FillArea = true });
            image.Crop(target, target, Gravity.Center);
        }
        else
        {
            image.Resize(new MagickGeometry(target, target));
            image.Extent(target, target, Gravity.Center);
        }

        image.ResetPage();
        image.Strip();
        image.Format = MagickFormat.Png32;
        return image.ToByteArray();
    }

    public static string BuildManifest(string appName, string themeColor, string backgroundColor)
    {
        var manifest = new Dictionary<string, object>
        {
            ["name"] = appName,
            ["short_name"] = appName.Length > 12 ? appName[..12] : appName,
            ["icons"] = new[]
            {
                new Dictionary<string, string> { ["src"] = "android-chrome-192x192.png", ["sizes"] = "192x192", ["type"] = "image/png" },
                new Dictionary<string, string> { ["src"] = "android-chrome-512x512.png", ["sizes"] = "512x512", ["type"] = "image/png" },
            },
            ["theme_color"] = themeColor,
            ["background_color"] = backgroundColor,
            ["display"] = "standalone",
        };
        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    public static string BuildHtmlSnippet(string themeColor) =>
        $"""
        <link rel="icon" href="/favicon.ico" sizes="48x48">
        <link rel="icon" type="image/png" sizes="16x16" href="/favicon-16x16.png">
        <link rel="icon" type="image/png" sizes="32x32" href="/favicon-32x32.png">
        <link rel="icon" type="image/png" sizes="48x48" href="/favicon-48x48.png">
        <link rel="apple-touch-icon" sizes="180x180" href="/apple-touch-icon.png">
        <link rel="manifest" href="/site.webmanifest">
        <meta name="theme-color" content="{themeColor}">

        """;

    private static string Color(string? value) => value is not null && HexColor.IsMatch(value) ? value : "#ffffff";

    private static void WriteNew(string path, byte[] content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(content);
    }
}
