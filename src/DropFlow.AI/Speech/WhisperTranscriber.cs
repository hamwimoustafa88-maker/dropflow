using Whisper.net;

namespace DropFlow.AI.Speech;

/// <param name="Language">"auto" or an ISO 639-1 code ("ar", "en", ...).</param>
public sealed record TranscribeSettings(string Language = "auto", bool TranslateToEnglish = false, int? Threads = null);

public sealed record TranscriptionResult(IReadOnlyList<TranscriptSegment> Segments, string? DetectedLanguage);

/// <summary>Turns 16 kHz mono samples into timed text. The production implementation is Whisper.net; tests substitute a fake.</summary>
public interface ISpeechTranscriber
{
    Task<TranscriptionResult> TranscribeAsync(
        string modelPath, float[] samples, TranscribeSettings settings, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>Thrown when the native Whisper runtime or the model file cannot be loaded or run.</summary>
public sealed class SpeechEngineException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Runs Whisper.net (whisper.cpp) on the CPU. The loaded model is cached between calls because loading a 500 MB model takes seconds;
/// runs are serialised because one run already uses every core.
/// </summary>
public sealed class WhisperNetTranscriber : ISpeechTranscriber, IDisposable
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idleTimer;
    private WhisperFactory? _factory;
    private string? _factoryPath;

    /// <param name="idleTimeout">How long a loaded model is kept after the last run before its memory is released.</param>
    public WhisperNetTranscriber(TimeSpan? idleTimeout = null)
    {
        IdleTimeout = idleTimeout ?? DefaultIdleTimeout;
        _idleTimer = new Timer(_ => ReleaseIfIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public TimeSpan IdleTimeout { get; }

    /// <summary>True while a model is held in memory.</summary>
    public bool IsModelLoaded => _factory is not null;

    public async Task<TranscriptionResult> TranscribeAsync(
        string modelPath, float[] samples, TranscribeSettings settings, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try
        {
            var factory = GetFactory(modelPath);
            var builder = factory.CreateBuilder()
                .WithLanguage(settings.Language is { Length: > 0 } l ? l : "auto")
                .WithThreads(settings.Threads ?? Math.Clamp(Environment.ProcessorCount, 1, 8));
            if (settings.TranslateToEnglish) builder = builder.WithTranslate();
            if (progress is not null) builder = builder.WithProgressHandler(p => progress.Report(Math.Clamp(p / 100.0, 0, 1)));

            await using var processor = builder.Build();   // the model is parsed here, so a damaged file surfaces here
            var segments = new List<TranscriptSegment>();
            string? detected = null;
            await foreach (var seg in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                segments.Add(new TranscriptSegment(seg.Start, seg.End, seg.Text));
                if (!string.IsNullOrEmpty(seg.Language)) detected ??= seg.Language;
            }
            return new TranscriptionResult(segments, detected);
        }
        catch (WhisperModelLoadException ex)
        {
            DropFactory();   // never cache a model that failed to load
            throw new SpeechEngineException($"Whisper could not load the model '{Path.GetFileName(modelPath)}'. The file may be damaged; delete it and download it again. ({ex.Message})", ex);
        }
        catch (WhisperProcessingException ex)
        {
            throw new SpeechEngineException($"Whisper failed while transcribing: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw new SpeechEngineException($"The Whisper runtime could not be loaded on this PC: {ex.Message}", ex);
        }
        finally
        {
            if (_factory is not null) _idleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    private void ReleaseIfIdle()
    {
        // A run in progress holds the gate; it re-arms the timer when it finishes.
        if (!_gate.Wait(0)) return;
        try
        {
            DropFactory();
        }
        finally
        {
            _gate.Release();
        }
    }

    private WhisperFactory GetFactory(string modelPath)
    {
        if (_factory is not null && string.Equals(_factoryPath, modelPath, StringComparison.OrdinalIgnoreCase)) return _factory;
        DropFactory();

        // whisper.cpp opens the file with the ANSI code page on some builds, so a model folder under an Arabic user name
        // could not be opened by path. Reading it ourselves and loading from memory is immune to that.
        _factory = modelPath.All(c => c < 128)
            ? WhisperFactory.FromPath(modelPath)
            : WhisperFactory.FromBuffer(File.ReadAllBytes(modelPath));
        _factoryPath = modelPath;
        return _factory;
    }

    private void DropFactory()
    {
        _factory?.Dispose();
        _factory = null;
        _factoryPath = null;
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        DropFactory();
        _gate.Dispose();
    }
}
