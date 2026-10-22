namespace DropFlow.AI.Models;

public enum ModelPurpose
{
    SpeechToText,
    BackgroundRemoval,
    FaceDetection,
    Translation,
}

/// <summary>
/// One file of a model that consists of several files (a "pack": network halves, vocabulary, tokenizer). Every file is pinned by
/// size and SHA-256 and lives in the pack's folder under the models folder.
/// </summary>
/// <param name="Name">Plain file name inside the pack folder (no sub-folders).</param>
public sealed record ModelFile(string Name, Uri Url, long SizeBytes, string Sha256);

/// <summary>
/// One downloadable AI model. Models are never bundled and never fetched implicitly: the user chooses to download one,
/// <see cref="ModelManager"/> does it, and every download is checked against <see cref="Sha256"/> before it is installed.
/// </summary>
public sealed record ModelInfo
{
    /// <summary>Stable id used in options and converter error messages (e.g. "ggml-small").</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }
    public required ModelPurpose Purpose { get; init; }

    /// <summary>File name inside the models folder; for a pack (see <see cref="Files"/>) the name of the pack's folder.</summary>
    public required string FileName { get; init; }

    /// <summary>Where the file comes from; for a pack the address of its first file (shown to the user as the download host).</summary>
    public required Uri Url { get; init; }

    /// <summary>
    /// Exact size in bytes; shown to the user before downloading and used to detect truncated or wrong files.
    /// For a pack it is the total of all its files.
    /// </summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Non-empty for a model made of several files (translation models: encoder, decoder, vocabulary, tokenizer). Then
    /// <see cref="Sha256"/> stays null and every file carries its own hash, <see cref="SizeBytes"/> is their sum and
    /// <see cref="FileName"/> names the folder that holds them.
    /// </summary>
    public IReadOnlyList<ModelFile> Files { get; init; } = [];

    public bool IsPack => Files.Count > 0;

    /// <summary>
    /// Lower-case hex SHA-256 of the file. When null the manager refuses to download unless <see cref="AllowUnverified"/> is set,
    /// because an unverified model could be swapped by whoever controls the URL.
    /// </summary>
    public string? Sha256 { get; init; }

    public bool AllowUnverified { get; init; }

    /// <summary>SPDX identifier of the weights' licence.</summary>
    public required string License { get; init; }

    /// <summary>Where the file comes from (model card), for attribution in the UI and THIRD-PARTY-NOTICES.</summary>
    public required string SourcePage { get; init; }

    public string Description { get; init; } = "";

    public bool IsVerified => IsPack ? Files.All(f => f.Sha256.Length == 64) : Sha256 is not null;
}

/// <summary>The built-in model list. URLs are pinned to an immutable Hugging Face commit so the pinned SHA-256 stays valid.</summary>
public static class ModelCatalog
{
    // Hashes and sizes below come from the Hugging Face file metadata (git-lfs oid = SHA-256) on 2026-10-21; the files were NOT
    // downloaded while writing this. ModelManager re-verifies the hash of every download before installing it.
    private const string WhisperRev = "5359861c739e955e79d9a303bcbc70fb988958b1";
    private const string WhisperRepo = "https://huggingface.co/ggerganov/whisper.cpp";

    private static ModelInfo Whisper(string stem, string display, long size, string sha, string description) => new()
    {
        Id = stem,
        DisplayName = display,
        Purpose = ModelPurpose.SpeechToText,
        FileName = stem + ".bin",
        Url = new Uri($"{WhisperRepo}/resolve/{WhisperRev}/{stem}.bin"),
        SizeBytes = size,
        Sha256 = sha,
        License = "MIT",
        SourcePage = WhisperRepo,
        Description = description,
    };

    // Translation: Opus-MT (Helsinki-NLP, Tatoeba/OPUS data) in the ONNX export of Xenova/transformers.js, int8-quantised encoder and merged decoder
    // (about 113 MB per direction). Sizes and hashes were computed from real downloads of exactly these revisions on 2026-10-23 (the large files
    // also match the git-lfs oid Hugging Face reports). The weights are published under Apache-2.0 on the Hugging Face model cards of the
    // original Helsinki-NLP repositories; the upstream OPUS-MT project describes its models as CC-BY-4.0. Both are permissive; the attribution
    // in THIRD-PARTY-NOTICES.md satisfies both. NLLB (CC-BY-NC) is deliberately not used.
    private const string EnArRev = "034a684356c19021a187cda4d7f823298b913921";
    private const string ArEnRev = "09c7556866400dcc53562ba8d7035119e7d9a2c1";

    private static ModelInfo OpusMt(string pair, string rev, string display, string description,
        (string Name, string Remote, long Size, string Sha)[] files)
    {
        var repo = $"https://huggingface.co/Xenova/opus-mt-{pair}";
        var list = files.Select(f => new ModelFile(f.Name, new Uri($"{repo}/resolve/{rev}/{f.Remote}"), f.Size, f.Sha)).ToList();
        return new ModelInfo
        {
            Id = "opus-mt-" + pair,
            DisplayName = display,
            Purpose = ModelPurpose.Translation,
            FileName = "opus-mt-" + pair,
            Url = list[0].Url,
            SizeBytes = list.Sum(f => f.SizeBytes),
            Files = list,
            License = "Apache-2.0",
            SourcePage = repo,
            Description = description,
        };
    }

    public static IReadOnlyList<ModelInfo> Default { get; } =
    [
        OpusMt("en-ar", EnArRev, "Opus-MT English to Arabic (translation)",
            "108 MB. Translates English text, subtitles and images into Arabic. Plain and short sentences work best; names and slang may be off.",
            [
                ("encoder_model.onnx", "onnx/encoder_model_int8.onnx", 51_600_664, "045dce89bd4b5bd5d8f546b9e3f9b506a9dbae70c551e8744eac67564050ddfd"),
                ("decoder_model_merged.onnx", "onnx/decoder_model_merged_int8.onnx", 58_878_333, "f2cd3894f105e5859d60cd66ad5f83356a3343e68f60c1997627a6e2d156f549"),
                ("source.spm", "source.spm", 801_074, "81ac8f4d4ee271f4109eac67560a31f9c2d555463ba5eb95c32c6deee8ae819d"),
                ("vocab.json", "vocab.json", 2_243_581, "8b74af9e4c1ec122b8d06362db6c38795e10ca2b0156b2110cf08d4716744b76"),
            ]),
        OpusMt("ar-en", ArEnRev, "Opus-MT Arabic to English (translation)",
            "108 MB. Translates Arabic text, subtitles and images into English. Good on everyday and news-style sentences.",
            [
                ("encoder_model.onnx", "onnx/encoder_model_int8.onnx", 51_617_048, "c95b80aad377b35481f5c828b984c5a05a08c5dc97767bea6d219eb80f982e3c"),
                ("decoder_model_merged.onnx", "onnx/decoder_model_merged_int8.onnx", 58_894_845, "e782fb4f915303df4bcbded070eb4fb7cbd2b58103c6d04654e265f6334d4f0e"),
                ("source.spm", "source.spm", 917_407, "484f7210e5f4466f7e5b99b660717c6ef2a6f90746196daba507795b69dc02a2"),
                ("vocab.json", "vocab.json", 2_257_246, "87b78c097c331672ca22a522c3ba24c5dc2cdff90495dcb52cf742635439a3a0"),
            ]),

        Whisper("ggml-tiny", "Whisper tiny (multilingual)", 77_691_713,
            "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21",
            "Fastest, lowest accuracy. Fine for quick drafts in English; weak for Arabic."),
        Whisper("ggml-base", "Whisper base (multilingual)", 147_951_465,
            "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
            "Fast, modest accuracy. Light machines and clear English speech."),
        Whisper("ggml-base.en", "Whisper base (English only)", 147_964_211,
            "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002",
            "Slightly better than ggml-base for English; cannot transcribe other languages."),
        Whisper("ggml-small", "Whisper small (multilingual)", 487_601_967,
            "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
            "Good balance. The smallest model that is usable for Arabic."),
        Whisper("ggml-small.en", "Whisper small (English only)", 487_614_201,
            "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d",
            "Best small model for English; cannot transcribe other languages."),
        Whisper("ggml-medium-q5_0", "Whisper medium (multilingual, 5-bit)", 539_212_467,
            "19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f",
            "High accuracy and supports translation to English. Needs about 4 GB free RAM."),
        Whisper("ggml-large-v3-turbo-q5_0", "Whisper large-v3-turbo (multilingual, 5-bit)", 574_041_195,
            "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2",
            "Best accuracy per second, including Arabic. Cannot translate to English (use medium for that)."),

        new ModelInfo
        {
            Id = "u2netp",
            DisplayName = "U²-Net-P (background removal, fast)",
            Purpose = ModelPurpose.BackgroundRemoval,
            FileName = "u2netp.onnx",
            Url = new Uri("https://huggingface.co/BritishWerewolf/U-2-Netp/resolve/7112208dbac3a3642496c8d54e2f0f9bb3dc1dc8/onnx/model.onnx"),
            SizeBytes = 4_574_861,
            Sha256 = "309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8",
            License = "Apache-2.0",
            SourcePage = "https://huggingface.co/BritishWerewolf/U-2-Netp",
            Description = "4 MB. Quick cut-outs of clear subjects (people, products). Edges are soft on fine detail such as hair.",
        },
        new ModelInfo
        {
            Id = "birefnet-lite",
            DisplayName = "BiRefNet-lite (background removal, high quality)",
            Purpose = ModelPurpose.BackgroundRemoval,
            FileName = "birefnet-lite.onnx",
            Url = new Uri("https://huggingface.co/onnx-community/BiRefNet_lite-ONNX/resolve/de15b22ba131738a16dff04aab8bdf8dc32e3ac1/onnx/model.onnx"),
            SizeBytes = 224_005_088,
            Sha256 = "5600024376f572a557870a5eb0afb1e5961636bef4e1e22132025467d0f03333",
            License = "MIT",
            SourcePage = "https://huggingface.co/onnx-community/BiRefNet_lite-ONNX",
            Description = "214 MB. Much sharper edges (hair, fur, thin objects); slower and needs about 2 GB RAM.",
        },
        new ModelInfo
        {
            Id = "ultraface-rfb-320",
            DisplayName = "UltraFace RFB-320 (face detection)",
            Purpose = ModelPurpose.FaceDetection,
            FileName = "ultraface-rfb-320.onnx",
            Url = new Uri("https://huggingface.co/Arm/ultraface-rfb-320-onnx-raspberry/resolve/43df978324197a69e6d2ed09a5c80afbb7e2d039/ultraface-rfb-320_raspberry_onnx_original.onnx"),
            SizeBytes = 1_121_822,
            Sha256 = "360f8c2d9c070f0a8d28b2bf6b3a3ec5ad69625618796f049908522e2275cd4a",
            License = "MIT",
            SourcePage = "https://huggingface.co/Arm/ultraface-rfb-320-onnx-raspberry",
            Description = "1 MB. Finds frontal and slightly turned faces for auto-redaction; may miss very small or heavily turned faces.",
        },
    ];

    public static ModelInfo? Find(string id) => Default.FirstOrDefault(m => m.Id == id);
}
