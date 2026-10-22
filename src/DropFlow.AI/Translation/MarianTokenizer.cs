using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace DropFlow.AI.Translation;

/// <summary>
/// The tokenizer of an Opus-MT (MarianMT) model. Text is cut into SentencePiece pieces with <c>source.spm</c> (the normalisation, NFKC and
/// the like, is part of that file) and every piece is mapped to its id in the shared <c>vocab.json</c>; pieces the vocabulary does not know become
/// the unknown token. Decoding needs no SentencePiece model: ids go back to pieces, which are joined, and the word-start mark is turned into a space.
/// </summary>
public sealed class MarianTokenizer
{
    public const string WordStart = "▁";   // "▁", SentencePiece's mark for "a space came before this piece"

    private readonly SentencePieceTokenizer _spm;
    private readonly Dictionary<string, int> _ids;
    private readonly string[] _pieces;

    public int EosId { get; }
    public int PadId { get; }
    public int UnkId { get; }
    public int VocabularySize => _pieces.Length;

    private MarianTokenizer(SentencePieceTokenizer spm, Dictionary<string, int> ids, string[] pieces)
    {
        _spm = spm;
        _ids = ids;
        _pieces = pieces;
        EosId = ids["</s>"];
        PadId = ids["<pad>"];
        UnkId = ids["<unk>"];
    }

    public static MarianTokenizer Load(string sourceSpmPath, string vocabJsonPath)
    {
        Dictionary<string, int> ids;
        try
        {
            using var vocab = File.OpenRead(vocabJsonPath);
            ids = JsonSerializer.Deserialize<Dictionary<string, int>>(vocab) ?? throw new InvalidDataException("vocab.json is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("vocab.json is not valid JSON: " + ex.Message, ex);
        }
        foreach (var special in new[] { "</s>", "<pad>", "<unk>" })
            if (!ids.ContainsKey(special)) throw new InvalidDataException($"vocab.json has no '{special}' entry.");

        var pieces = new string[ids.Values.Max() + 1];
        Array.Fill(pieces, "");
        foreach (var (piece, id) in ids) pieces[id] = piece;

        using var spmStream = File.OpenRead(sourceSpmPath);
        var spm = SentencePieceTokenizer.Create(spmStream, addBeginningOfSentence: false, addEndOfSentence: false);
        return new MarianTokenizer(spm, ids, pieces);
    }

    /// <summary>Id of a language tag such as <c>&gt;&gt;ara&lt;&lt;</c>, or null when the vocabulary has no such tag.</summary>
    public int? IdOf(string piece) => _ids.TryGetValue(piece, out var id) ? id : null;

    /// <summary>Source ids for the encoder: optional language tag, the pieces of <paramref name="text"/>, then the end-of-sentence id.</summary>
    public long[] Encode(string text, int? languageTagId = null)
    {
        var ids = new List<long>(text.Length / 3 + 4);
        if (languageTagId is { } tag) ids.Add(tag);
        if (!string.IsNullOrWhiteSpace(text))
        {
            foreach (var token in _spm.EncodeToTokens(text, out _))
                ids.Add(_ids.TryGetValue(token.Value, out var id) ? id : UnkId);
        }
        ids.Add(EosId);
        return [.. ids];
    }

    /// <summary>Number of source ids <paramref name="text"/> needs (including the end mark), without allocating the id list.</summary>
    public int CountTokens(string text) => string.IsNullOrWhiteSpace(text) ? 1 : _spm.EncodeToTokens(text, out _).Count + 1;

    /// <summary>Joins generated ids into text. Special tokens (end, padding, unknown) are dropped.</summary>
    public string Decode(IEnumerable<long> ids)
    {
        var sb = new StringBuilder();
        foreach (var raw in ids)
        {
            if (raw < 0 || raw >= _pieces.Length) continue;
            var id = (int)raw;
            if (id == EosId || id == PadId || id == UnkId) continue;
            sb.Append(_pieces[id]);
        }
        return sb.Replace(WordStart, " ").ToString().Trim();
    }
}
