using System.Text.RegularExpressions;
using DropFlow.AI.Models;
using Xunit;

namespace DropFlow.AI.Tests;

/// <summary>The built-in model list is data that ships to users; these checks keep it honest.</summary>
public class ModelCatalogTests
{
    private static readonly string[] AllowedLicences = ["MIT", "Apache-2.0", "BSD-3-Clause", "BSD-2-Clause"];

    [Fact]
    public void Ids_and_file_names_are_unique()
    {
        var all = ModelCatalog.Default;
        Assert.Equal(all.Count, all.Select(m => m.Id).Distinct().Count());
        Assert.Equal(all.Count, all.Select(m => m.FileName.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void Every_entry_is_pinned_sized_and_licensed_for_redistribution()
    {
        foreach (var m in ModelCatalog.Default)
        {
            Assert.True(m.SizeBytes > 0, m.Id);
            Assert.Equal("https", m.Url.Scheme);
            Assert.Contains(m.License, AllowedLicences);
            Assert.False(m.AllowUnverified, $"{m.Id} must be verified");
            Assert.True(m.IsVerified, m.Id);
            Assert.False(Path.IsPathRooted(m.FileName) || m.FileName.Contains('/') || m.FileName.Contains('\\'), $"{m.Id}: file name must be plain");

            if (m.IsPack)
            {
                // a model of several files: every file is pinned on its own and the sizes add up to what the user is told
                Assert.Null(m.Sha256);
                Assert.Equal(m.Files.Sum(f => f.SizeBytes), m.SizeBytes);
                Assert.Equal(m.Files.Count, m.Files.Select(f => f.Name).Distinct().Count());
                Assert.Equal(m.Files[0].Url, m.Url);
                foreach (var f in m.Files)
                {
                    Assert.True(f.SizeBytes > 0, $"{m.Id}/{f.Name}");
                    Assert.Equal("https", f.Url.Scheme);
                    Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
                    Assert.False(f.Name.Contains('/') || f.Name.Contains('\\') || f.Name.Contains(".."), $"{m.Id}/{f.Name}: file name must be plain");
                }
            }
            else
            {
                Assert.NotNull(m.Sha256);
                Assert.Matches("^[0-9a-f]{64}$", m.Sha256!);
            }
        }
    }

    [Fact]
    public void Hugging_face_urls_are_pinned_to_a_commit_not_to_a_moving_branch()
    {
        var urls = ModelCatalog.Default.SelectMany(m => m.IsPack ? m.Files.Select(f => f.Url) : [m.Url]);
        foreach (var url in urls.Where(u => u.Host == "huggingface.co"))
            Assert.Matches(new Regex("/resolve/[0-9a-f]{40}/"), url.AbsolutePath);
    }

    [Fact]
    public void No_non_commercial_models_are_listed()
    {
        // RMBG-1.4 / RMBG-2.0 are research-or-non-commercial licensed and must never be offered.
        foreach (var m in ModelCatalog.Default)
        {
            Assert.DoesNotContain("rmbg", m.Id, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rmbg", m.Url.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("briaai", m.Url.ToString(), StringComparison.OrdinalIgnoreCase);
            // NLLB and other research-only translation models are CC-BY-NC and must never be offered either.
            Assert.DoesNotContain("nllb", m.Id + m.Url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("facebook", m.Url.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_two_translation_models_are_listed_as_packs_with_what_the_translator_reads()
    {
        foreach (var direction in new[] { DropFlow.AI.Translation.TranslationDirection.EnglishToArabic, DropFlow.AI.Translation.TranslationDirection.ArabicToEnglish })
        {
            var m = DropFlow.AI.Translation.TranslationInfo.Model(direction)!;
            Assert.Equal(ModelPurpose.Translation, m.Purpose);
            Assert.Equal("Apache-2.0", m.License);                    // the licence of the Opus-MT weights on their Hugging Face model card
            Assert.True(m.IsPack);
            Assert.Equal(
                new[] { DropFlow.AI.Translation.MarianTextTranslator.EncoderFile, DropFlow.AI.Translation.MarianTextTranslator.DecoderFile, DropFlow.AI.Translation.MarianTextTranslator.SourceSpmFile, DropFlow.AI.Translation.MarianTextTranslator.VocabularyFile }.Order(),
                m.Files.Select(f => f.Name).Order());
            Assert.InRange(m.SizeBytes, 100_000_000, 130_000_000);     // about 108 MB: the quantised encoder and decoder, not the 400 MB originals
            Assert.StartsWith("https://huggingface.co/Xenova/opus-mt-", m.SourcePage);
        }
        Assert.Equal("opus-mt-en-ar", DropFlow.AI.Translation.TranslationInfo.EnArModelId);
        Assert.Equal("opus-mt-ar-en", DropFlow.AI.Translation.TranslationInfo.ArEnModelId);
    }

    [Fact]
    public void Every_purpose_has_a_model_and_whisper_ids_match_the_choice_table()
    {
        foreach (var purpose in Enum.GetValues<ModelPurpose>())
            Assert.Contains(ModelCatalog.Default, m => m.Purpose == purpose);

        var whisper = ModelCatalog.Default.Where(m => m.Purpose == ModelPurpose.SpeechToText).Select(m => m.Id).Order().ToList();
        var profiles = DropFlow.AI.Speech.WhisperModelChoice.Profiles.Select(p => p.ModelId).Order().ToList();
        Assert.Equal(profiles, whisper);
    }

    [Fact]
    public void Ids_that_the_converters_depend_on_exist()
    {
        foreach (var id in new[] { "ggml-tiny", "ggml-small", "u2netp", "birefnet-lite", "ultraface-rfb-320" })
            Assert.NotNull(ModelCatalog.Find(id));
        Assert.Equal(DropFlow.AI.Imaging.RedactAutoConverter.FaceModelId, "ultraface-rfb-320");
    }

    [Fact]
    public void The_default_folder_is_the_data_folder_not_the_install_folder()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropFlow Data", "models");
        Assert.Equal(expected, ModelManager.DefaultDirectory);
        using var m = new ModelManager();
        Assert.Equal(expected, m.Directory);
    }
}
