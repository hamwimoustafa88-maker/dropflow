using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;
using Markdig;
using Markdig.Renderers;
using ReverseMarkdown;

namespace DropFlow.Engines.Pdf.Office;

/// <summary>
/// Word-processor style conversions that need no engine: txt -> docx/html/md, md -> docx/html/txt, docx -> txt/md/html, html -> md/txt.
/// Everything goes through one rich model (headings, paragraphs, nested lists, tables, bold/italic/code/links) so each reader works with each writer.
/// </summary>
internal static partial class DocumentTools
{
    // The single list of in-process document pairs lives in ManagedPairs (LibreOffice is never asked for these); EPUB is handled by EpubTools.
    private static readonly (string Input, string[] Targets)[] Pairs = ManagedPairs.Documents.Where(d => d.Input != "epub").ToArray();

    public static IEnumerable<OperationSpec> Specs()
    {
        var targets = Pairs.SelectMany(p => p.Targets).Distinct();
        foreach (var target in targets)
        {
            var inputs = Pairs.Where(p => p.Targets.Contains(target)).Select(p => p.Input).ToArray();
            yield return new OperationSpec($"convert:{target}", inputs, ctx => Task.FromResult(Convert(ctx, target)));
        }
    }

    private static ConversionResult Convert(JobContext ctx, string target)
    {
        var source = Path.GetExtension(ctx.Input).TrimStart('.').ToLowerInvariant();
        var title = Path.GetFileNameWithoutExtension(ctx.Input);
        ctx.Report(0.2);

        string output;
        switch (source, target)
        {
            case ("md", "html"):
                output = ctx.ReserveFile(ctx.Input, "html", "converted");
                File.WriteAllText(output, MarkdownToHtml(TextFile.Read(ctx.Input), title), TextFile.Utf8);
                break;

            case ("html", "md"):
                output = ctx.ReserveFile(ctx.Input, "md", "converted");
                File.WriteAllText(output, HtmlToMarkdown(ReadHtml(ctx.Input)), TextFile.Utf8);
                break;

            case ("html", "txt"):
                output = ctx.ReserveFile(ctx.Input, "txt", "converted");
                File.WriteAllText(output, RichTextWriter.Write(MarkdownReader.Parse(HtmlToMarkdown(ReadHtml(ctx.Input)))), TextFile.Utf8WithBom);
                break;

            default:
                var document = source switch
                {
                    "txt" => PlainTextReader.Read(ctx.Input),
                    "md" => MarkdownReader.Read(ctx.Input),
                    "docx" => DocxReader.Read(ctx.Input),
                    _ => throw new ConversionFailure($"{Path.GetFileName(ctx.Input)}: .{source} cannot be read."),
                };
                ctx.Ct.ThrowIfCancellationRequested();
                ctx.Report(0.6);
                output = ctx.ReserveFile(ctx.Input, target, "converted");
                Write(document, target, output, document.Title ?? title);
                break;
        }

        ctx.Report(1);
        return ctx.Ok(output);
    }

    private static void Write(RichDocument document, string target, string path, string title)
    {
        var language = LanguageOf(document);
        switch (target)
        {
            case "docx": RichDocxWriter.Write(document, path, title, language); break;
            case "html": File.WriteAllText(path, RichHtmlWriter.Write(document, title, language), TextFile.Utf8); break;
            case "md": File.WriteAllText(path, RichMarkdownWriter.Write(document), TextFile.Utf8); break;
            case "txt": File.WriteAllText(path, RichTextWriter.Write(document), TextFile.Utf8WithBom); break;
            default: throw new ConversionFailure($"Cannot write .{target}.");
        }
    }

    internal static string LanguageOf(RichDocument document)
    {
        var sample = string.Concat(document.Blocks.Take(60).Select(b => b switch
        {
            RHeading h => RichText.Plain(h.Runs),
            RParagraph p => RichText.Plain(p.Runs),
            RListItem l => RichText.Plain(l.Runs),
            _ => "",
        }));
        return LanguageOf(sample);
    }

    internal static string LanguageOf(string text)
    {
        var arabic = text.Count(c => c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' or >= 'ﭐ' and <= '﻿');
        var hebrew = text.Count(c => c is >= '֐' and <= '׿');
        var latin = text.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        if (arabic > latin && arabic >= hebrew) return "ar";
        if (hebrew > latin) return "he";
        return "en";
    }

    // ---- markdown <-> html ----------------------------------------------------------------------------------------------

    private static readonly MarkdownPipeline HtmlPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    [GeneratedRegex(@"^\s*#\s+(?<t>.+?)\s*#*\s*$", RegexOptions.Multiline)]
    private static partial Regex FirstHeading();

    /// <summary>
    /// Full Markdig rendering (images, footnotes, task lists, tables). Raw HTML in the Markdown is escaped and only ordinary link
    /// schemes are kept, so a Markdown file from anywhere cannot put a script into the page.
    /// </summary>
    internal static string MarkdownToHtml(string markdown, string fallbackTitle)
    {
        var document = Markdown.Parse(markdown, HtmlPipeline);
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer) { LinkRewriter = SafeUrl };
        HtmlPipeline.Setup(renderer);
        renderer.Render(document);
        var body = writer.ToString();

        var heading = FirstHeading().Match(markdown);
        var title = heading.Success ? heading.Groups["t"].Value : fallbackTitle;
        var language = LanguageOf(markdown);

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(language).Append("\" dir=\"auto\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n<style>\n");
        sb.Append("body{font-family:system-ui,'Segoe UI',Arial,sans-serif;line-height:1.6;max-width:46rem;margin:2rem auto;padding:0 1rem;color:#1a1a1a}\n");
        sb.Append("p,li,h1,h2,h3,h4,h5,h6,blockquote,td,th,dt,dd{unicode-bidi:plaintext;text-align:start}\n");
        sb.Append("table{border-collapse:collapse;margin:1rem 0}th,td{border:1px solid #999;padding:.3rem .6rem}th{background:#f0f0f0}\n");
        sb.Append("pre{background:#f4f4f4;padding:.8rem;overflow:auto}code{font-family:Consolas,'Courier New',monospace}img{max-width:100%}\n");
        sb.Append("blockquote{border-inline-start:3px solid #bbb;margin-inline:0;padding-inline-start:1rem;color:#444}\n</style>\n</head>\n<body>\n");
        sb.Append(body);
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    private static string SafeUrl(string url)
    {
        var trimmed = url.Trim();
        var colon = trimmed.IndexOf(':');
        if (colon < 0) return url;
        var scheme = trimmed[..colon].ToLowerInvariant();
        return scheme is "http" or "https" or "mailto" or "tel" or "ftp" || (scheme == "data" && trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase))
            ? url
            : "#";
    }

    private static readonly Converter MarkdownConverter = new(CreateConfig());

    private static Config CreateConfig()
    {
        var config = new Config { GithubFlavored = true };
        config.Tags.Unknown = Config.UnknownTagsOption.Bypass;
        config.Formatting.RemoveComments = true;
        config.Links.SmartHref = true;
        config.Images.Base64Handling = Config.Base64ImageHandling.Skip; // a data: image would turn a small page into a huge Markdown file
        config.Links.WhitelistedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto", "tel", "ftp", "" }) config.Links.WhitelistedSchemes.Add(scheme); // "" = relative links
        return config;
    }

    internal static string HtmlToMarkdown(string html)
    {
        var markdown = MarkdownConverter.Convert(html);
        return markdown.Replace("\r\n", "\n").Trim() + "\n";
    }

    private static readonly Regex MetaCharset = new(@"<meta[^>]+charset\s*=\s*[""']?\s*(?<cs>[\w\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Reads an HTML file, honouring a byte order mark or a &lt;meta charset&gt; declaration before falling back to UTF-8 / legacy detection.</summary>
    internal static string ReadHtml(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 2 && (bytes[0] == 0xEF && bytes[1] == 0xBB || bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF);
        if (!hasBom)
        {
            var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            var match = MetaCharset.Match(head);
            if (match.Success)
            {
                try
                {
                    var encoding = Encoding.GetEncoding(match.Groups["cs"].Value);
                    if (encoding.CodePage != 65001) return encoding.GetString(bytes); // a declared non-UTF-8 charset wins
                }
                catch (ArgumentException)
                {
                    // unknown charset name: fall through to detection
                }
            }
        }
        return TextFile.Decode(bytes);
    }
}
