using DropFlow.Engines.Archive.Archives;

namespace DropFlow.Engines.Archive.Tests;

public class SafeEntryPathTests
{
    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("a/b/../../../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("a\\..\\..\\evil.txt")]
    [InlineData("..")]
    [InlineData("a/..")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32\\evil.dll")]
    [InlineData("//server/share/evil.txt")]
    [InlineData("\\\\server\\share\\evil.txt")]
    [InlineData("\\\\?\\C:\\evil.txt")]
    [InlineData("C:\\Windows\\evil.txt")]
    [InlineData("c:/Windows/evil.txt")]
    [InlineData("C:evil.txt")]
    [InlineData("D:")]
    [InlineData("a/.../b.txt")]
    [InlineData("a/. ./b.txt")]
    [InlineData("a/ /b.txt")]
    [InlineData("a\0b.txt")]
    public void Escaping_or_ambiguous_paths_are_unsafe(string name)
    {
        var result = SafeEntryPath.Evaluate(name);
        Assert.Equal(EntryPathStatus.Unsafe, result.Status);
        Assert.False(string.IsNullOrEmpty(result.Reason));
    }

    [Theory]
    [InlineData("file.txt", "file.txt")]
    [InlineData("dir/file.txt", "dir/file.txt")]
    [InlineData("dir\\file.txt", "dir/file.txt")]
    [InlineData("./dir/./file.txt", "dir/file.txt")]
    [InlineData("dir//file.txt", "dir/file.txt")]
    [InlineData("dir/", "dir")]
    [InlineData(".hidden", ".hidden")]
    [InlineData("a b/c (1).tar.gz", "a b/c (1).tar.gz")]
    public void Ordinary_paths_are_kept(string name, string expected)
    {
        var result = SafeEntryPath.Evaluate(name);
        Assert.Equal(EntryPathStatus.Ok, result.Status);
        Assert.Equal(expected, result.RelativePath);
    }

    [Fact]
    public void Arabic_and_other_unicode_names_pass_through_unchanged()
    {
        var name = $"{Kit.ArabicFolder}/{Kit.ArabicFile}.txt";
        var result = SafeEntryPath.Evaluate(name);
        Assert.Equal(EntryPathStatus.Ok, result.Status);
        Assert.Equal(name, result.RelativePath);

        // Zero-width joiners are part of correct Arabic/Persian spelling and must not be stripped.
        var joined = "\u0645\u200D\u0644\u200C\u0641.txt";
        Assert.Equal(joined, SafeEntryPath.Evaluate(joined).RelativePath);
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con", "_con")]
    [InlineData("NUL", "_NUL")]
    [InlineData("aux.txt", "_aux.txt")]
    [InlineData("COM1", "_COM1")]
    [InlineData("com9.log", "_com9.log")]
    [InlineData("LPT3.tar.gz", "_LPT3.tar.gz")]
    [InlineData("dir/PRN", "dir/_PRN")]
    [InlineData("CONIN$", "_CONIN$")]
    [InlineData("CON .txt", "_CON .txt")]
    public void Reserved_device_names_are_neutralised(string name, string expected)
    {
        var result = SafeEntryPath.Evaluate(name);
        Assert.Equal(EntryPathStatus.Sanitized, result.Status);
        Assert.Equal(expected, result.RelativePath);
    }

    [Theory]
    [InlineData("console.txt")]
    [InlineData("nullable.cs")]
    [InlineData("com10.txt")]
    [InlineData("auxiliary")]
    public void Names_that_only_start_like_device_names_are_fine(string name) =>
        Assert.Equal(EntryPathStatus.Ok, SafeEntryPath.Evaluate(name).Status);

    [Theory]
    [InlineData("secret.txt:hidden", "secret.txt_hidden")]
    [InlineData("a/b:c/d.txt", "a/b_c/d.txt")]
    [InlineData("what?.txt", "what_.txt")]
    [InlineData("a<b>.txt", "a_b_.txt")]
    [InlineData("trailing.txt. ", "trailing.txt")]
    [InlineData("pipe|name", "pipe_name")]
    [InlineData("tab\there.txt", "tab_here.txt")]
    public void Windows_hostile_characters_are_replaced(string name, string expected)
    {
        var result = SafeEntryPath.Evaluate(name);
        Assert.Equal(EntryPathStatus.Sanitized, result.Status);
        Assert.Equal(expected, result.RelativePath);
    }

    [Fact]
    public void Right_to_left_override_that_fakes_an_extension_is_neutralised()
    {
        // "invoice<RLO>fdp.exe" is displayed as "invoiceexe.pdf"
        var result = SafeEntryPath.Evaluate("invoice\u202Efdp.exe");
        Assert.Equal(EntryPathStatus.Sanitized, result.Status);
        Assert.DoesNotContain('\u202E', result.RelativePath);
    }

    [Fact]
    public void Very_long_segments_are_shortened_and_keep_their_extension()
    {
        var result = SafeEntryPath.Evaluate(new string('x', 400) + ".pdf");
        Assert.Equal(EntryPathStatus.Sanitized, result.Status);
        Assert.True(result.RelativePath.Length <= 200);
        Assert.EndsWith(".pdf", result.RelativePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("./")]
    [InlineData(".")]
    public void Empty_names_are_skipped(string? name) =>
        Assert.Equal(EntryPathStatus.Skip, SafeEntryPath.Evaluate(name).Status);

    [Fact]
    public void ResolveUnder_refuses_anything_that_leaves_the_root()
    {
        using var temp = new DropFlow.TestSupport.TempDir();
        var root = SafeEntryPath.WithTrailingSeparator(temp.Path);

        Assert.StartsWith(root, SafeEntryPath.ResolveUnder(root, "a/b.txt"), StringComparison.OrdinalIgnoreCase);
        var ex = Assert.Throws<ArchiveException>(() => SafeEntryPath.ResolveUnder(root, "../outside.txt"));
        Assert.Equal(ArchiveProblem.UnsafePath, ex.Problem);
        Assert.Throws<ArchiveException>(() => SafeEntryPath.ResolveUnder(root, "a/../../outside.txt"));
    }
}
