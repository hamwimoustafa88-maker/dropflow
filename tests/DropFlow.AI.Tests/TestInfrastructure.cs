using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using DropFlow.AI.Models;
using DropFlow.AI.Ocr;
using DropFlow.Core.Operations;
using Xunit;

namespace DropFlow.AI.Tests;

/// <summary>
/// A fact that is skipped, visibly, when the named model has not been downloaded to the default models folder
/// (<c>%LOCALAPPDATA%\DropFlow\models</c>) or the folder in the DROPFLOW_MODELS_DIR environment variable. Tests never download models.
/// </summary>
public sealed class ModelFactAttribute : FactAttribute
{
    public ModelFactAttribute(string modelId)
    {
        var dir = Environment.GetEnvironmentVariable("DROPFLOW_MODELS_DIR");
        using var manager = new ModelManager(string.IsNullOrEmpty(dir) ? null : dir);
        if (manager.Find(modelId) is null)
        {
            Skip = $"Unknown model id '{modelId}'.";
            return;
        }
        if (!manager.IsInstalled(modelId))
            Skip = $"Model '{modelId}' is not downloaded ({manager.PathOf(modelId)}). Download it through the app's model manager to run this test.";
    }

    public static ModelManager OpenManager()
    {
        var dir = Environment.GetEnvironmentVariable("DROPFLOW_MODELS_DIR");
        return new ModelManager(string.IsNullOrEmpty(dir) ? null : dir);
    }
}

/// <summary>Skipped unless Windows has the OCR language pack (default "en-US"; pass "ar" to need any Arabic pack).</summary>
public sealed class OcrFactAttribute : FactAttribute
{
    public OcrFactAttribute(string language = "en")
    {
        if (!new OcrLanguageService().IsInstalled(language))
        {
            var hint = OcrLanguageService.InstallHint(language);
            Skip = $"Windows OCR language '{language}' is not installed" + (hint is null ? "." : $" (admin PowerShell: {hint.PowerShellCommand}).");
        }
    }
}

/// <summary>Engine provider whose answers the test decides.</summary>
public sealed class FakeEngines(params (EngineId Id, string Path)[] installed) : IEngineProvider
{
    public string? Locate(EngineId engine) => installed.Where(e => e.Id == engine).Select(e => e.Path).FirstOrDefault();
}

/// <summary>
/// A real HTTP server on the loopback interface (System.Net.HttpListener) with Range support, used to test the model downloader
/// without touching the internet.
/// </summary>
public sealed class TestHttpServer : IDisposable
{
    public sealed record Request(string Path, string? Range);

    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, byte[]> _files = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _cutsRemaining;
    private int _cutAfter;

    public ConcurrentQueue<Request> Requests { get; } = new();

    /// <summary>When false the server ignores Range headers and always answers 200 with the whole body.</summary>
    public bool SupportRange { get; set; } = true;

    /// <summary>When set, every answer gets this status instead (e.g. 404, 500).</summary>
    public HttpStatusCode? ForceStatus { get; set; }

    /// <summary>When above zero the body is sent in chunks of this size with <see cref="ChunkDelayMs"/> between them.</summary>
    public int ChunkBytes { get; set; }

    public int ChunkDelayMs { get; set; } = 10;

    public string BaseUrl { get; }

    public TestHttpServer()
    {
        // Pick a free port, then bind HttpListener to it (loopback prefixes need no URL ACL).
        for (var attempt = 0; ; attempt++)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                BaseUrl = $"http://127.0.0.1:{port}/";
                break;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                // port taken between probe and bind: try another
            }
        }
        _loop = Task.Run(Loop);
    }

    public Uri Add(string path, byte[] content)
    {
        _files[path.TrimStart('/')] = content;
        return new Uri(BaseUrl + path.TrimStart('/'));
    }

    /// <summary>Makes the next <paramref name="times"/> answers hang up after sending only <paramref name="afterBytes"/> body bytes.</summary>
    public void CutNextResponses(int times, int afterBytes)
    {
        _cutAfter = afterBytes;
        _cutsRemaining = times;
    }

    private async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening) { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url!.AbsolutePath.TrimStart('/');
            var range = ctx.Request.Headers["Range"];
            Requests.Enqueue(new Request(path, range));

            if (ForceStatus is { } forced) { ctx.Response.StatusCode = (int)forced; ctx.Response.Close(); return; }
            if (!_files.TryGetValue(path, out var data)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }

            long start = 0;
            var status = 200;
            if (SupportRange && range is { } r && r.StartsWith("bytes=", StringComparison.Ordinal))
            {
                start = long.Parse(r["bytes=".Length..].TrimEnd('-'));
                if (start >= data.Length)
                {
                    ctx.Response.StatusCode = 416;
                    ctx.Response.AddHeader("Content-Range", $"bytes */{data.Length}");
                    ctx.Response.Close();
                    return;
                }
                status = 206;
                ctx.Response.AddHeader("Content-Range", $"bytes {start}-{data.Length - 1}/{data.Length}");
            }

            var count = (int)(data.Length - start);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentLength64 = count;
            ctx.Response.AddHeader("Accept-Ranges", SupportRange ? "bytes" : "none");

            if (Interlocked.Decrement(ref _cutsRemaining) >= 0)
            {
                var part = Math.Min(_cutAfter, count);
                ctx.Response.OutputStream.Write(data, (int)start, part);
                ctx.Response.OutputStream.Flush();
                Thread.Sleep(150); // let the client drain what was sent before the connection drops
                ctx.Response.Abort();
                return;
            }

            if (ChunkBytes > 0)
            {
                // Slow drip so tests can cancel in the middle of a transfer.
                for (var sent = 0; sent < count; sent += ChunkBytes)
                {
                    ctx.Response.OutputStream.Write(data, (int)start + sent, Math.Min(ChunkBytes, count - sent));
                    ctx.Response.OutputStream.Flush();
                    Thread.Sleep(ChunkDelayMs);
                }
            }
            else
            {
                ctx.Response.OutputStream.Write(data, (int)start, count);
            }
            ctx.Response.Close();
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
            // shutting down
        }
        catch (HttpListenerException)
        {
            // client hung up (expected in cancellation tests)
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        try { _loop.Wait(2000); } catch (AggregateException) { }
        _stop.Dispose();
    }
}

public static class Hashing
{
    public static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Deterministic pseudo-random bytes so a flipped byte is detectable.</summary>
    public static byte[] Bytes(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }
}

public static class TestImages
{
    /// <summary>
    /// A JPEG whose EXIF Orientation is 6 ("rotate 90 degrees clockwise to display"): stored width x height, shown height x width.
    /// The EXIF segment is spliced in by hand so the test does not depend on how Magick.NET would write it.
    /// </summary>
    public static string JpegRotated90(string path, int width, int height, ImageMagick.IMagickColor<byte> color)
    {
        byte[] jpeg;
        using (var src = new ImageMagick.MagickImage(color, (uint)width, (uint)height))
        {
            src.Format = ImageMagick.MagickFormat.Jpeg;
            jpeg = src.ToByteArray();
        }
        byte[] app1 =
        [
            0xFF, 0xE1, 0x00, 0x22,                         // APP1, length 34
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0, 0, 0, 8,   // big-endian TIFF header, IFD0 at offset 8
            0x00, 0x01,                                     // one entry
            0x01, 0x12, 0x00, 0x03, 0, 0, 0, 1, 0x00, 0x06, 0, 0, // Orientation, SHORT, count 1, value 6
            0, 0, 0, 0,                                     // no next IFD
        ];
        using var output = new MemoryStream();
        output.Write(jpeg, 0, 2);                           // SOI
        output.Write(app1);
        output.Write(jpeg, 2, jpeg.Length - 2);
        File.WriteAllBytes(path, output.ToArray());
        return path;
    }
}
