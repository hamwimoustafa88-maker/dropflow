using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DropFlow.AI.Translation;

/// <summary>
/// An Opus-MT (MarianMT) network as two ONNX files: the encoder and the "merged" decoder (one graph that serves both the first step and the
/// cached steps). Generation is batched beam search with a key/value cache; <c>beams = 1</c> is greedy decoding.
/// Not thread-safe: callers serialise their calls.
/// </summary>
internal sealed class MarianModel : IDisposable
{
    /// <summary>Position table size of the Marian models: no sequence, source or generated, may be longer.</summary>
    public const int MaxPositions = 512;

    private const int NoRepeatNgram = 4;
    private const double LengthPenalty = 1.0;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly int _layers;
    private readonly int _heads;
    private readonly int _headDim;
    private readonly int _hidden;
    private readonly int _padId;
    private readonly int _eosId;
    private readonly string[] _pastNames;       // layer-major: decoder.key, decoder.value, encoder.key, encoder.value
    private readonly string[] _presentNames;

    private MarianModel(InferenceSession encoder, InferenceSession decoder, int padId, int eosId)
    {
        _encoder = encoder;
        _decoder = decoder;
        _padId = padId;
        _eosId = eosId;

        var past = decoder.InputMetadata.Keys.Where(k => k.StartsWith("past_key_values.", StringComparison.Ordinal)).ToList();
        _layers = past.Count / 4;
        if (_layers == 0 || past.Count % 4 != 0)
            throw new InvalidDataException("The decoder is not a merged Marian decoder (no past_key_values inputs).");
        var shape = decoder.InputMetadata[past[0]].Dimensions;
        _heads = shape[1];
        _headDim = shape[3];
        _hidden = encoder.OutputMetadata["last_hidden_state"].Dimensions[2];
        _pastNames = new string[_layers * 4];
        _presentNames = new string[_layers * 4];
        string[] kinds = ["decoder.key", "decoder.value", "encoder.key", "encoder.value"];
        for (var l = 0; l < _layers; l++)
            for (var k = 0; k < 4; k++)
            {
                _pastNames[l * 4 + k] = $"past_key_values.{l}.{kinds[k]}";
                _presentNames[l * 4 + k] = $"present.{l}.{kinds[k]}";
            }
    }

    public static MarianModel Load(string encoderPath, string decoderPath, int padId, int eosId)
    {
        InferenceSession? encoder = null;
        try
        {
            using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            encoder = new InferenceSession(encoderPath, options);
            var decoder = new InferenceSession(decoderPath, options);
            return new MarianModel(encoder, decoder, padId, eosId);
        }
        catch (OnnxRuntimeException ex)
        {
            encoder?.Dispose();
            throw new TranslationUnavailableException("The translation model is damaged or not a valid ONNX file: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Translates a batch. <paramref name="sources"/> are encoder ids that already end with the end-of-sentence id. Returns, per source,
    /// the generated ids without the start and end ids.
    /// </summary>
    public long[][] Generate(IReadOnlyList<long[]> sources, int beams, CancellationToken ct)
    {
        var batch = sources.Count;
        if (batch == 0) return [];
        var width = Math.Max(1, beams);
        var rows = batch * width;
        var srcLen = sources.Max(s => s.Length);
        if (srcLen > MaxPositions) throw new ArgumentException($"A source of {srcLen} ids is longer than the {MaxPositions} the model supports.", nameof(sources));
        var maxNew = Math.Min(MaxPositions - 1, srcLen * 2 + 16);

        // ---- encoder -----------------------------------------------------------------------------------------------
        var inputIds = new long[batch * srcLen];
        var mask = new long[batch * srcLen];
        Array.Fill(inputIds, (long)_padId);
        for (var b = 0; b < batch; b++)
            for (var i = 0; i < sources[b].Length; i++)
            {
                inputIds[b * srcLen + i] = sources[b][i];
                mask[b * srcLen + i] = 1;
            }

        float[] hiddenSmall;
        using (var encoded = _encoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, [batch, srcLen])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, [batch, srcLen])),
        ]))
        {
            hiddenSmall = encoded.First(v => v.Name == "last_hidden_state").AsTensor<float>().ToArray();
        }
        ct.ThrowIfCancellationRequested();

        // Every hypothesis of a source reads the same encoder output: repeat each source's rows `width` times.
        var hidden = new float[rows * srcLen * _hidden];
        var maskRows = new long[rows * srcLen];
        var perSource = srcLen * _hidden;
        for (var b = 0; b < batch; b++)
            for (var k = 0; k < width; k++)
            {
                var r = b * width + k;
                Array.Copy(hiddenSmall, b * perSource, hidden, r * perSource, perSource);
                Array.Copy(mask, b * srcLen, maskRows, r * srcLen, srcLen);
            }
        var hiddenTensor = new DenseTensor<float>(hidden, [rows, srcLen, _hidden]);
        var maskTensor = new DenseTensor<long>(maskRows, [rows, srcLen]);

        // ---- beam search state -------------------------------------------------------------------------------------
        var sequences = new List<long>[rows];
        var scores = new double[rows];
        for (var r = 0; r < rows; r++)
        {
            sequences[r] = [];
            scores[r] = r % width == 0 ? 0 : -1e9;     // only the first hypothesis is alive at the start, or all would be copies
        }
        var finished = new List<(double Score, long[] Tokens)>[batch];
        var done = new bool[batch];
        for (var b = 0; b < batch; b++) finished[b] = [];

        var past = new float[_layers * 4][];            // null at the first step
        var pastLength = 0;                            // decoder positions already cached
        var nextInput = new long[rows];
        Array.Fill(nextInput, (long)_padId);            // the decoder starts with the padding id

        for (var step = 0; step < maxNew; step++)
        {
            ct.ThrowIfCancellationRequested();
            var inputs = new List<NamedOnnxValue>(_pastNames.Length + 4)
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(nextInput, [rows, 1])),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hiddenTensor),
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", maskTensor),
                NamedOnnxValue.CreateFromTensor("use_cache_branch", new DenseTensor<bool>(new[] { step > 0 }, [1])),
            };
            for (var i = 0; i < _pastNames.Length; i++)
            {
                var isEncoderPart = i % 4 >= 2;
                var length = step == 0 ? 0 : isEncoderPart ? srcLen : pastLength;
                var data = past[i] ?? [];
                inputs.Add(NamedOnnxValue.CreateFromTensor(_pastNames[i], new DenseTensor<float>(data, [rows, _heads, length, _headDim])));
            }

            float[] logits;
            var nextPast = new float[_pastNames.Length][];
            using (var results = _decoder.Run(inputs))
            {
                var byName = results.ToDictionary(v => v.Name);
                logits = byName["logits"].AsTensor<float>().ToArray();
                for (var i = 0; i < _presentNames.Length; i++)
                {
                    var isEncoderPart = i % 4 >= 2;
                    // The encoder half of the cache never changes after the first step, so later outputs for it are ignored.
                    nextPast[i] = isEncoderPart && step > 0 ? past[i]! : byName[_presentNames[i]].AsTensor<float>().ToArray();
                }
            }
            pastLength++;
            var vocab = logits.Length / rows;

            // ---- choose the next hypotheses ------------------------------------------------------------------------
            var source = new int[rows];
            var chosen = new long[rows];
            for (var b = 0; b < batch; b++)
            {
                var first = b * width;
                if (done[b])
                {
                    for (var k = 0; k < width; k++) { source[first + k] = first + k; chosen[first + k] = _padId; }
                    continue;
                }

                var candidates = new List<(int Row, int Token, double Score)>();
                for (var k = 0; k < width; k++)
                {
                    if (scores[first + k] < -1e8) continue;      // a dead slot (fewer live hypotheses than the beam width)
                    TopCandidates(logits, (first + k) * vocab, vocab, scores[first + k], first + k, 2 * width + 2, candidates);
                }
                candidates.Sort((a, c) => c.Score.CompareTo(a.Score));

                var next = new List<(int Row, int Token, double Score)>();
                var rank = 0;
                foreach (var cand in candidates)
                {
                    if (next.Count == width) break;
                    var seq = sequences[cand.Row];
                    if (cand.Token == _eosId)
                    {
                        if (rank < width) finished[b].Add((cand.Score / Math.Pow(seq.Count + 1, LengthPenalty), [.. seq]));
                        rank++;
                        continue;
                    }
                    if (RepeatsNgram(seq, cand.Token)) continue;
                    next.Add(cand);
                    rank++;
                }

                var lastStep = step == maxNew - 1;
                if (next.Count == 0 || lastStep)
                {
                    foreach (var cand in next) finished[b].Add((cand.Score / Math.Pow(sequences[cand.Row].Count + 1, LengthPenalty), [.. sequences[cand.Row], cand.Token]));
                    if (finished[b].Count == 0) finished[b].Add((0, [.. sequences[first]]));
                    done[b] = true;
                    for (var k = 0; k < width; k++) { source[first + k] = first + k; chosen[first + k] = _padId; }
                    continue;
                }

                // A source is complete when it has enough finished hypotheses and none of the running ones can still beat the worst of them.
                if (finished[b].Count >= width)
                {
                    var worst = finished[b].Min(f => f.Score);
                    var bestRunning = next[0].Score / Math.Pow(sequences[next[0].Row].Count + 1, LengthPenalty);
                    if (worst >= bestRunning)
                    {
                        done[b] = true;
                        for (var k = 0; k < width; k++) { source[first + k] = first + k; chosen[first + k] = _padId; }
                        continue;
                    }
                }

                for (var k = 0; k < width; k++)
                {
                    var pick = next[Math.Min(k, next.Count - 1)];   // fewer survivors than slots: repeat the last one (it will be dropped later)
                    source[first + k] = pick.Row;
                    chosen[first + k] = pick.Token;
                }
                // Commit the new sequences and scores for this source.
                var newSeqs = new List<long>[width];
                var newScores = new double[width];
                for (var k = 0; k < width; k++)
                {
                    var pick = next[Math.Min(k, next.Count - 1)];
                    newSeqs[k] = [.. sequences[pick.Row], pick.Token];
                    newScores[k] = k < next.Count ? pick.Score : -1e9;
                }
                for (var k = 0; k < width; k++) { sequences[first + k] = newSeqs[k]; scores[first + k] = newScores[k]; }
            }

            if (done.All(d => d)) break;

            // Hypotheses moved: reorder the cached keys and values to follow them.
            var identity = true;
            for (var r = 0; r < rows; r++) if (source[r] != r) { identity = false; break; }
            for (var i = 0; i < nextPast.Length; i++)
                past[i] = identity || i % 4 >= 2 ? nextPast[i] : GatherRows(nextPast[i], rows, source);
            nextInput = chosen;
        }

        var output = new long[batch][];
        for (var b = 0; b < batch; b++)
            output[b] = finished[b].Count == 0 ? [] : finished[b].OrderByDescending(f => f.Score).First().Tokens;
        return output;
    }

    /// <summary>Adds the best <paramref name="count"/> continuations of one hypothesis (log-softmax plus its running score) to <paramref name="into"/>.</summary>
    private void TopCandidates(float[] logits, int offset, int vocab, double baseScore, int row, int count, List<(int Row, int Token, double Score)> into)
    {
        var max = float.NegativeInfinity;
        for (var v = 0; v < vocab; v++) if (v != _padId && logits[offset + v] > max) max = logits[offset + v];
        double sum = 0;
        for (var v = 0; v < vocab; v++) if (v != _padId) sum += Math.Exp(logits[offset + v] - max);
        var logZ = max + Math.Log(sum);

        var best = new (int Token, float Logit)[count];
        var filled = 0;
        for (var v = 0; v < vocab; v++)
        {
            if (v == _padId) continue;
            var logit = logits[offset + v];
            if (filled == count && logit <= best[filled - 1].Logit) continue;
            var at = filled < count ? filled++ : count - 1;
            while (at > 0 && best[at - 1].Logit < logit) { best[at] = best[at - 1]; at--; }
            best[at] = (v, logit);
        }
        for (var i = 0; i < filled; i++) into.Add((row, best[i].Token, baseScore + best[i].Logit - logZ));
    }

    /// <summary>True when appending <paramref name="token"/> would repeat an n-gram the hypothesis already contains (stops translation loops).</summary>
    private static bool RepeatsNgram(List<long> sequence, int token)
    {
        var n = NoRepeatNgram;
        if (sequence.Count < n) return false;
        var start = sequence.Count - (n - 1);
        for (var i = 0; i + n <= sequence.Count; i++)
        {
            if (sequence[i + n - 1] != token) continue;
            var same = true;
            for (var j = 0; j < n - 1; j++)
                if (sequence[i + j] != sequence[start + j]) { same = false; break; }
            if (same) return true;
        }
        return false;
    }

    /// <summary>New tensor whose row r is row <c>source[r]</c> of <paramref name="data"/> (rows are the first dimension).</summary>
    private static float[] GatherRows(float[] data, int rows, int[] source)
    {
        var rowSize = data.Length / rows;
        var result = new float[data.Length];
        for (var r = 0; r < rows; r++) Array.Copy(data, source[r] * rowSize, result, r * rowSize, rowSize);
        return result;
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
