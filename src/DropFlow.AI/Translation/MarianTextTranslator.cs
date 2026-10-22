using System.Text;
using DropFlow.AI.Models;

namespace DropFlow.AI.Translation;

/// <summary>
/// <see cref="ITextTranslator"/> backed by the Opus-MT models of <see cref="ModelManager"/> (English to Arabic and Arabic to English),
/// run with ONNX Runtime on the CPU. Everything happens on this PC. A loaded model is released after a few idle minutes.
/// </summary>
public sealed class MarianTextTranslator : ITextTranslator, IDisposable
{
    public const string EncoderFile = "encoder_model.onnx";
    public const string DecoderFile = "decoder_model_merged.onnx";
    public const string SourceSpmFile = "source.spm";
    public const string VocabularyFile = "vocab.json";

    /// <summary>The target-language tag the English-to-Arabic model needs in front of its input (it knows several Arabic varieties).</summary>
    public const string ArabicTag = ">>ara<<";

    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(3);

    private sealed record Loaded(MarianModel Model, MarianTokenizer Tokenizer, int? TagId);

    private readonly ModelManager _models;
    private readonly object _gate = new();
    private readonly Dictionary<TranslationDirection, Loaded> _loaded = [];
    private readonly Timer _idleTimer;
    private readonly TimeSpan _idleTimeout;
    private bool _disposed;

    public MarianTextTranslator(ModelManager models, TimeSpan? idleTimeout = null)
    {
        _models = models;
        _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        _idleTimer = new Timer(_ => ReleaseIfIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Number of models currently held in memory.</summary>
    public int LoadedCount
    {
        get { lock (_gate) return _loaded.Count; }
    }

    public string? MissingModel(TranslationDirection direction)
    {
        var id = TranslationInfo.ModelId(direction);
        return _models.IsInstalled(id) ? null : id;
    }

    public string? NotReadyMessage(TranslationDirection direction) =>
        MissingModel(direction) is { } id ? AiFailures.MissingModel(_models, id) : null;

    public async Task<IReadOnlyList<string>> TranslateAsync(
        TranslationDirection direction, IReadOnlyList<string> texts, TranslationQuality quality, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (NotReadyMessage(direction) is { } notReady)
            throw new TranslationUnavailableException(notReady);

        // 1) Cut every text into kept pieces and sentences; collect the sentences that need the model.
        var perText = new List<List<TextSegmenter.Piece>>(texts.Count);
        var work = new Dictionary<string, string?>(StringComparer.Ordinal);      // sentence -> translation (null until done)
        foreach (var text in texts)
        {
            var pieces = TextSegmenter.Split(text);
            perText.Add(pieces);
            foreach (var p in pieces)
                if (p.IsSentence && ScriptDetector.NeedsTranslation(p.Text, direction)) work.TryAdd(p.Text, null);
        }

        // 2) Translate the distinct sentences, similar lengths together so the padding stays small.
        if (work.Count > 0)
        {
            var translations = await Task.Run(() => RunBatches(direction, [.. work.Keys], quality, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            foreach (var (sentence, translation) in translations) work[sentence] = translation;
        }
        progress?.Report(1);

        // 3) Put the pieces back together.
        var result = new List<string>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            var sb = new StringBuilder();
            foreach (var p in perText[i])
                sb.Append(p.IsSentence && work.TryGetValue(p.Text, out var t) && t is not null ? t : p.Text);
            result.Add(sb.ToString());
        }
        return result;
    }

    private Dictionary<string, string> RunBatches(
        TranslationDirection direction, List<string> sentences, TranslationQuality quality, IProgress<double>? progress, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            try
            {
                var loaded = Load(direction);
                var beams = (int)quality;

                var encoded = sentences
                    .Select(s => (Text: s, Ids: loaded.Tokenizer.Encode(s, loaded.TagId)))
                    .Select(e => e.Ids.Length > MarianModel.MaxPositions - 1 ? (e.Text, Ids: e.Ids[..(MarianModel.MaxPositions - 2)].Append((long)loaded.Tokenizer.EosId).ToArray()) : e)
                    .OrderBy(e => e.Ids.Length)
                    .ToList();

                var results = new Dictionary<string, string>(StringComparer.Ordinal);
                var totalTokens = Math.Max(1, encoded.Sum(e => e.Ids.Length));
                long doneTokens = 0;
                var index = 0;
                while (index < encoded.Count)
                {
                    ct.ThrowIfCancellationRequested();
                    // Batch limits: sentences and a token budget (hypotheses multiply the work for beam search).
                    var batch = new List<(string Text, long[] Ids)>();
                    var maxLen = 0;
                    while (index < encoded.Count && batch.Count < MaxBatchSentences(beams))
                    {
                        var next = encoded[index];
                        var newMax = Math.Max(maxLen, next.Ids.Length);
                        if (batch.Count > 0 && newMax * (batch.Count + 1) * beams > TokenBudget) break;
                        batch.Add(next);
                        maxLen = newMax;
                        index++;
                    }

                    var generated = loaded.Model.Generate([.. batch.Select(b => b.Ids)], beams, ct);
                    for (var i = 0; i < batch.Count; i++)
                    {
                        var text = TextSegmenter.Finish(batch[i].Text, loaded.Tokenizer.Decode(generated[i]), direction);
                        results[batch[i].Text] = text.Length == 0 ? batch[i].Text : text;   // an empty answer must not erase text
                        doneTokens += batch[i].Ids.Length;
                    }
                    progress?.Report(Math.Min(0.99, (double)doneTokens / totalTokens));
                }
                return results;
            }
            finally
            {
                if (_loaded.Count > 0 && !_disposed) _idleTimer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private const int TokenBudget = 1600;

    private static int MaxBatchSentences(int beams) => Math.Max(1, 32 / beams);

    private Loaded Load(TranslationDirection direction)
    {
        if (_loaded.TryGetValue(direction, out var hit)) return hit;
        var id = TranslationInfo.ModelId(direction);
        try
        {
            var tokenizer = MarianTokenizer.Load(_models.FilePathOf(id, SourceSpmFile), _models.FilePathOf(id, VocabularyFile));
            int? tag = null;
            if (direction == TranslationDirection.EnglishToArabic)
                tag = tokenizer.IdOf(ArabicTag) ?? throw new TranslationUnavailableException($"The vocabulary of '{id}' has no {ArabicTag} tag; delete the model and download it again.");
            var model = MarianModel.Load(_models.FilePathOf(id, EncoderFile), _models.FilePathOf(id, DecoderFile), tokenizer.PadId, tokenizer.EosId);
            var loaded = new Loaded(model, tokenizer, tag);
            _loaded[direction] = loaded;
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or KeyNotFoundException)
        {
            throw new TranslationUnavailableException($"The translation model '{id}' could not be read ({ex.Message}). Delete it in the model manager and download it again.", ex);
        }
    }

    private void ReleaseIfIdle()
    {
        if (!Monitor.TryEnter(_gate)) return;   // a run is in progress; the timer is re-armed when it ends
        try
        {
            if (_disposed) return;
            DisposeModels();
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private void DisposeModels()
    {
        foreach (var l in _loaded.Values) l.Model.Dispose();
        _loaded.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _idleTimer.Dispose();
            DisposeModels();
        }
    }
}
