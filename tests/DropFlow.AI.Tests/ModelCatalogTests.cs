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
            Assert.NotNull(m.Sha256);
            Assert.Matches("^[0-9a-f]{64}$", m.Sha256!);
            Assert.False(m.AllowUnverified, $"{m.Id} must be verified");
            Assert.False(Path.IsPathRooted(m.FileName) || m.FileName.Contains('/') || m.FileName.Contains('\\'), $"{m.Id}: file name must be plain");
        }
    }

    [Fact]
    public void Hugging_face_urls_are_pinned_to_a_commit_not_to_a_moving_branch()
    {
        foreach (var m in ModelCatalog.Default.Where(m => m.Url.Host == "huggingface.co"))
            Assert.Matches(new Regex("/resolve/[0-9a-f]{40}/"), m.Url.AbsolutePath);
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
        }
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
    public void The_default_folder_is_under_local_app_data()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropFlow", "models");
        Assert.Equal(expected, ModelManager.DefaultDirectory);
        using var m = new ModelManager();
        Assert.Equal(expected, m.Directory);
    }
}
