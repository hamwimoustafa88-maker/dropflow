using DropFlow.Core.Operations;
using DropFlow.Engines.Archive.General.Rename;
using DropFlow.TestSupport;

using static DropFlow.TestSupport.TestPaths;

namespace DropFlow.Engines.Archive.Tests;

public class RenamePlanTests
{
    private static readonly DateTime Now = new(2026, 3, 9, 14, 5, 30);

    /// <summary>Plans over paths that do not exist on disk: only the names matter, and nothing is touched.</summary>
    private static RenamePlan Plan(string[] names, RenameSpec spec, params string[] existingElsewhere)
    {
        var all = names.Select(n => P(@"C:\photos\") + n).ToArray();
        var existing = new HashSet<string>(all.Concat(existingElsewhere.Select(n => P(@"C:\photos\") + n)), StringComparer.OrdinalIgnoreCase);
        return BatchRename.Plan(all, spec, new RenamePlanOptions { Exists = existing.Contains, Now = Now });
    }

    private static string[] NewNames(RenamePlan plan) => plan.Items.Select(i => i.NewName).ToArray();

    // ------------------------------------------------------------------ tokens

    [Fact]
    public void Default_pattern_changes_nothing()
    {
        var plan = Plan(["a.jpg", "b.png"], new RenameSpec());
        Assert.Equal(["a.jpg", "b.png"], NewNames(plan));
        Assert.All(plan.Items, i => Assert.Equal(RenameIssue.Unchanged, i.Issue));
        Assert.False(plan.HasConflicts);
        Assert.Equal(0, plan.ChangeCount);
    }

    [Fact]
    public void Name_ext_and_index_tokens()
    {
        var plan = Plan(["a.jpg", "b.png", "readme"], new RenameSpec { Pattern = "trip_{n}_{name}{ext}" });
        Assert.Equal(["trip_1_a.jpg", "trip_2_b.png", "trip_3_readme"], NewNames(plan));
    }

    [Fact]
    public void A_dot_written_before_ext_is_not_doubled()
    {
        var plan = Plan(["a.jpg", "b"], new RenameSpec { Pattern = "x_{name}.{ext}" });
        Assert.Equal(["x_a.jpg", "x_b"], NewNames(plan));   // no "x_a..jpg", and no dangling dot for a file without extension
    }

    [Theory]
    [InlineData("{counter}", "1", "2", "3")]
    [InlineData("{counter:000}", "001", "002", "003")]
    [InlineData("img{counter:0000}", "img0001", "img0002", "img0003")]
    public void Counter_with_zero_padding(string pattern, string first, string second, string third)
    {
        var plan = Plan(["a.x", "b.x", "c.x"], new RenameSpec { Pattern = pattern + "{ext}" });
        Assert.Equal([first + ".x", second + ".x", third + ".x"], NewNames(plan));
    }

    [Fact]
    public void Counter_start_and_step()
    {
        var plan = Plan(["a.x", "b.x", "c.x"], new RenameSpec { Pattern = "{counter:00}{ext}", CounterStart = 10, CounterStep = 5 });
        Assert.Equal(["10.x", "15.x", "20.x"], NewNames(plan));

        var down = Plan(["a.x", "b.x"], new RenameSpec { Pattern = "{counter}{ext}", CounterStart = 5, CounterStep = -2 });
        Assert.Equal(["5.x", "3.x"], NewNames(down));
    }

    [Theory]
    [InlineData("{date}", "2026-03-09")]
    [InlineData("{date:yyyyMMdd}", "20260309")]
    [InlineData("{date:yyyy-MM-dd_HH.mm}", "2026-03-09_14.05")]
    [InlineData("{date:dd MMM yyyy}", "09 Mar 2026")]
    public void Date_token_with_formats(string pattern, string expected)
    {
        var plan = Plan(["a.x"], new RenameSpec { Pattern = pattern + "{ext}" });
        Assert.Equal(expected + ".x", plan.Items[0].NewName);
    }

    [Fact]
    public void Date_format_option_sets_the_default_for_plain_date_tokens()
    {
        var plan = Plan(["a.x"], new RenameSpec { Pattern = "{date}_{name}{ext}", DateFormat = "yyyyMMdd" });
        Assert.Equal("20260309_a.x", plan.Items[0].NewName);
    }

    [Fact]
    public void Parent_and_original_tokens()
    {
        var plan = Plan(["a.jpg"], new RenameSpec { Pattern = "{parent}-{original}" });
        Assert.Equal("photos-a.jpg", plan.Items[0].NewName);
    }

    [Fact]
    public void Doubled_braces_are_literal_braces()
    {
        var plan = Plan(["a.x"], new RenameSpec { Pattern = "{{{name}}}{ext}" });
        Assert.Equal("{a}.x", plan.Items[0].NewName);
    }

    [Theory]
    [InlineData("{bogus}{ext}")]
    [InlineData("{name")]
    [InlineData("name}{ext}")]
    public void Bad_patterns_are_reported_not_applied(string pattern)
    {
        var plan = Plan(["a.x", "b.x"], new RenameSpec { Pattern = pattern });
        Assert.True(plan.HasConflicts);
        Assert.NotEmpty(plan.Errors);
        Assert.All(plan.Items, i => Assert.Equal(RenameIssue.InvalidPattern, i.Issue));
    }

    [Fact]
    public void A_bad_date_format_is_reported()
    {
        var plan = Plan(["a.x"], new RenameSpec { Pattern = "{date:%}{ext}" });
        // a lone '%' is an invalid custom date format
        Assert.True(plan.HasConflicts);
    }

    // ------------------------------------------------------------------ find / replace

    [Fact]
    public void Find_and_replace_plain_text_is_case_sensitive_by_default()
    {
        var spec = new RenameSpec { Find = "IMG", Replace = "Holiday" };
        Assert.Equal(["Holiday_001.jpg", "img_002.jpg"], NewNames(Plan(["IMG_001.jpg", "img_002.jpg"], spec)));

        var insensitive = spec with { IgnoreCase = true };
        Assert.Equal(["Holiday_001.jpg", "Holiday_002.jpg"], NewNames(Plan(["IMG_001.jpg", "img_002.jpg"], insensitive)));
    }

    [Fact]
    public void Find_does_not_touch_the_extension_unless_asked()
    {
        var spec = new RenameSpec { Find = "jpg", Replace = "X" };
        Assert.Equal(["a.jpg"], NewNames(Plan(["a.jpg"], spec)));
        Assert.Equal(["a.X"], NewNames(Plan(["a.jpg"], spec with { FindInExtension = true })));
        Assert.Equal(["a.jpg"], NewNames(Plan(["a.jpeg"], new RenameSpec { Find = "jpeg", Replace = "jpg", FindInExtension = true })));
    }

    [Fact]
    public void Regex_with_groups_and_flags()
    {
        var spec = new RenameSpec { Find = @"^IMG_(\d+)_(\w+)$", Replace = "$2-$1", UseRegex = true };
        Assert.Equal(["beach-0042.jpg"], NewNames(Plan(["IMG_0042_beach.jpg"], spec)));

        var digits = new RenameSpec { Find = @"\d+", Replace = "#", UseRegex = true };
        Assert.Equal(["a#b#.txt"], NewNames(Plan(["a1b22.txt"], digits)));

        var insensitive = new RenameSpec { Find = "photo", Replace = "pic", UseRegex = true, IgnoreCase = true };
        Assert.Equal(["pic.jpg"], NewNames(Plan(["PHOTO.jpg"], insensitive)));
    }

    [Fact]
    public void An_invalid_regex_is_reported_with_a_message()
    {
        var plan = Plan(["a.x"], new RenameSpec { Find = "(unclosed", UseRegex = true });
        Assert.True(plan.HasConflicts);
        Assert.Contains("regular expression", plan.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_catastrophic_regex_is_stopped_by_the_timeout()
    {
        var evil = new string('a', 60) + "!";
        var plan = Plan([evil + ".txt"], new RenameSpec { Find = "(a+)+$", Replace = "x", UseRegex = true });
        Assert.True(plan.HasConflicts);
        Assert.Contains("too long", plan.Errors[0]);
    }

    // ------------------------------------------------------------------ case / trim / insert

    [Theory]
    [InlineData(CaseMode.Lower, "my holiday photo.jpg")]
    [InlineData(CaseMode.Upper, "MY HOLIDAY PHOTO.jpg")]
    [InlineData(CaseMode.Title, "My Holiday Photo.jpg")]
    [InlineData(CaseMode.Sentence, "My holiday photo.jpg")]
    public void Case_changes_apply_to_the_name_only(CaseMode mode, string expected)
    {
        var plan = Plan(["mY hOLIDAY phoTO.jpg"], new RenameSpec { Case = mode });
        Assert.Equal(expected, plan.Items[0].NewName);
    }

    [Fact]
    public void Title_case_handles_separators_and_apostrophes()
    {
        Assert.Equal("My-Holiday_Photo 2.jpg", Plan(["my-holiday_photo 2.jpg"], new RenameSpec { Case = CaseMode.Title }).Items[0].NewName);
        Assert.Equal("Don't Stop.txt", Plan(["don't stop.txt"], new RenameSpec { Case = CaseMode.Title }).Items[0].NewName);
    }

    [Fact]
    public void Lower_case_extension_option()
    {
        Assert.Equal("Photo.jpg", Plan(["Photo.JPG"], new RenameSpec { LowerCaseExtension = true }).Items[0].NewName);
    }

    [Fact]
    public void Arabic_names_pass_through_case_changes_and_replacement()
    {
        var plan = Plan([$"{Kit.ArabicFile}.TXT"], new RenameSpec { Case = CaseMode.Upper, Find = "\u0639\u0631\u0628\u064A", Replace = "\u0645\u0635\u0631\u064A" });
        Assert.Equal($"\u0645\u0644\u0641 \u0645\u0635\u0631\u064A.TXT", plan.Items[0].NewName);
    }

    [Fact]
    public void Trimming_removes_whitespace_given_characters_and_counts()
    {
        Assert.Equal("a b.txt", Plan(["  a b  .txt"], new RenameSpec { TrimWhitespace = true }).Items[0].NewName);
        Assert.Equal("report.txt", Plan(["__report--.txt"], new RenameSpec { TrimCharacters = "_-" }).Items[0].NewName);
        Assert.Equal("a b c.txt", Plan(["a   b    c.txt"], new RenameSpec { CollapseSpaces = true }).Items[0].NewName);
        Assert.Equal("photo.jpg", Plan(["IMG_photo.jpg"], new RenameSpec { RemoveFirst = 4 }).Items[0].NewName);
        Assert.Equal("photo.jpg", Plan(["photo_copy.jpg"], new RenameSpec { RemoveLast = 5 }).Items[0].NewName);
        Assert.Equal("x.jpg", Plan(["ab.jpg"], new RenameSpec { RemoveFirst = 9, Insert = "x" }).Items[0].NewName);   // removing more than exists leaves nothing to insert into
    }

    [Fact]
    public void Insert_at_a_position_from_the_start_or_the_end()
    {
        Assert.Equal("2026_photo.jpg", Plan(["photo.jpg"], new RenameSpec { Insert = "2026_" }).Items[0].NewName);
        Assert.Equal("pho-to.jpg", Plan(["photo.jpg"], new RenameSpec { Insert = "-", InsertAt = 3 }).Items[0].NewName);
        Assert.Equal("photo_final.jpg", Plan(["photo.jpg"], new RenameSpec { Insert = "_final", InsertAt = 0, InsertFromEnd = true }).Items[0].NewName);
        Assert.Equal("ph-oto.jpg", Plan(["photo.jpg"], new RenameSpec { Insert = "-", InsertAt = 3, InsertFromEnd = true }).Items[0].NewName);
        Assert.Equal("photo!.jpg", Plan(["photo.jpg"], new RenameSpec { Insert = "!", InsertAt = 99 }).Items[0].NewName); // beyond the end: appended
    }

    [Fact]
    public void Steps_run_in_the_documented_order_find_trim_insert_case_then_pattern()
    {
        var spec = new RenameSpec { Find = "draft", Replace = "final", TrimWhitespace = true, Insert = "new ", Case = CaseMode.Upper, Pattern = "{counter:00}-{name}{ext}" };
        Assert.Equal("01-NEW FINAL REPORT.docx", Plan([" draft report .docx"], spec).Items[0].NewName);
    }

    // ------------------------------------------------------------------ windows-safety of results

    [Theory]
    [InlineData("a:b.txt", "a_b.txt")]
    [InlineData("what?.txt", "what_.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("name.", "name")]
    public void New_names_are_made_valid_for_windows(string pattern, string expected)
    {
        var plan = Plan(["a.x"], new RenameSpec { Pattern = pattern });
        Assert.Equal(expected, plan.Items[0].NewName);
    }

    [Fact]
    public void A_pattern_cannot_smuggle_in_a_path()
    {
        var plan = Plan(["a.x"], new RenameSpec { Pattern = @"..\..\evil{ext}" });
        var item = plan.Items[0];
        Assert.DoesNotContain('\\', item.NewName);
        Assert.DoesNotContain('/', item.NewName);
        Assert.Equal(P(@"C:\photos"), Path.GetDirectoryName(item.TargetPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("...")]
    public void Names_that_end_up_empty_are_flagged(string pattern)
    {
        var plan = Plan(["a.x"], new RenameSpec { Pattern = pattern });
        Assert.Equal(RenameIssue.EmptyName, plan.Items[0].Issue);
        Assert.True(plan.HasConflicts);
    }
    // ------------------------------------------------------------------ conflicts

    [Fact]
    public void Two_files_getting_the_same_name_are_flagged_and_the_first_keeps_it()
    {
        var plan = Plan(["a.jpg", "b.jpg", "c.jpg"], new RenameSpec { Pattern = "same{ext}" });
        Assert.Equal(RenameIssue.None, plan.Items[0].Issue);
        Assert.Equal(RenameIssue.DuplicateTarget, plan.Items[1].Issue);
        Assert.Equal(RenameIssue.DuplicateTarget, plan.Items[2].Issue);
        Assert.True(plan.HasConflicts);
    }

    [Fact]
    public void Targets_that_differ_only_by_case_are_duplicates_on_windows()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { P(@"C:\in\A.jpg"), P(@"C:\in\a.JPG") };
        var plan = BatchRename.Plan([P(@"C:\in\A.jpg"), P(@"C:\in\a.JPG")], new RenameSpec(),
            new RenamePlanOptions { DestinationDirectory = P(@"C:\out"), Exists = existing.Contains });

        Assert.Equal(RenameIssue.None, plan.Items[0].Issue);
        Assert.Equal(RenameIssue.DuplicateTarget, plan.Items[1].Issue);
    }
    [Fact]
    public void A_name_already_used_by_another_file_is_flagged()
    {
        var plan = Plan(["a.jpg"], new RenameSpec { Pattern = "taken{ext}" }, "taken.jpg");
        Assert.Equal(RenameIssue.TargetExists, plan.Items[0].Issue);
        Assert.True(plan.HasConflicts);
    }

    [Fact]
    public void A_name_held_by_an_unchanged_file_in_the_selection_is_flagged()
    {
        // b.jpg stays b.jpg; renaming a.jpg to b.jpg would collide with it
        var plan = Plan(["a.jpg", "b.jpg"], new RenameSpec { Find = "a", Replace = "b" });
        Assert.Equal(RenameIssue.TargetExists, plan.Items[0].Issue);
        Assert.Equal(RenameIssue.Unchanged, plan.Items[1].Issue);
    }

    [Fact]
    public void A_chain_inside_the_selection_is_allowed_because_each_name_is_vacated_by_its_own_rename()
    {
        // 1.txt -> 2.txt, 2.txt -> 3.txt, 3.txt -> 4.txt
        var plan = Plan(["1.txt", "2.txt", "3.txt"], new RenameSpec { Pattern = "{counter}{ext}", CounterStart = 2 });

        Assert.Equal(["2.txt", "3.txt", "4.txt"], NewNames(plan));
        Assert.False(plan.HasConflicts);
        Assert.Equal(3, plan.ChangeCount);
    }

    [Fact]
    public void A_chain_that_runs_into_a_file_outside_the_selection_is_flagged()
    {
        var plan = Plan(["1.txt", "2.txt"], new RenameSpec { Pattern = "{counter}{ext}", CounterStart = 2 }, "3.txt");

        Assert.Equal(RenameIssue.None, plan.Items[0].Issue);
        Assert.Equal(RenameIssue.TargetExists, plan.Items[1].Issue);   // 2.txt -> 3.txt, but 3.txt belongs to someone else
    }
    [Fact]
    public void Uniquify_policy_adds_numbers_instead_of_reporting_conflicts()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { P(@"C:\in\a.jpg"), P(@"C:\in\b.jpg"), P(@"C:\out\same.jpg"), P(@"C:\out\same (2).jpg") };
        var plan = BatchRename.Plan([P(@"C:\in\a.jpg"), P(@"C:\in\b.jpg")], new RenameSpec { Pattern = "same{ext}" },
            new RenamePlanOptions { DestinationDirectory = P(@"C:\out"), Conflicts = ConflictPolicy.Uniquify, Exists = existing.Contains });

        Assert.False(plan.HasConflicts);
        Assert.Equal(["same (3).jpg", "same (4).jpg"], plan.Items.Select(i => i.NewName));
        Assert.All(plan.Items, i => Assert.True(i.Adjusted));
        Assert.All(plan.Items, i => Assert.Equal(P(@"C:\out"), Path.GetDirectoryName(i.TargetPath)));
    }

    [Fact]
    public void Missing_sources_are_flagged()
    {
        var plan = BatchRename.Plan([P(@"C:\nowhere\ghost.txt")], new RenameSpec { Pattern = "x{ext}" }, new RenamePlanOptions { Exists = _ => false });
        Assert.Equal(RenameIssue.MissingSource, plan.Items[0].Issue);
        Assert.True(plan.HasConflicts);
    }

    [Fact]
    public void The_preview_lists_old_arrow_new()
    {
        var plan = Plan(["a.jpg", "b.jpg"], new RenameSpec { Pattern = "trip_{counter:00}{ext}" });
        Assert.Equal(["a.jpg -> trip_01.jpg", "b.jpg -> trip_02.jpg"], plan.Preview());
    }

    [Fact]
    public void Planning_never_touches_the_disk()
    {
        using var temp = new TempDir();
        var file = temp.WriteText("a.txt", "x");
        var before = Directory.EnumerateFileSystemEntries(temp.Path).ToList();

        var plan = BatchRename.Plan([file], new RenameSpec { Pattern = "b{ext}" });

        Assert.Equal(before, Directory.EnumerateFileSystemEntries(temp.Path).ToList());
        Assert.Equal("b.txt", plan.Items[0].NewName);
    }

    [Fact]
    public void Natural_order_sorts_numbers_by_value_and_ignores_case()
    {
        var names = new[] { "file10.txt", "File2.txt", "file1.txt", "file02.txt", "b.txt", "a.txt" };
        Assert.Equal(["a.txt", "b.txt", "file1.txt", "File2.txt", "file02.txt", "file10.txt"], names.OrderBy(n => n, NaturalComparer.Instance));
    }
}

public class RenameApplyTests
{
    private static async Task<(string a, string b, string c)> ThreeFiles(TempDir temp)
    {
        var a = temp.WriteText("a.txt", "AAA");
        var b = temp.WriteText("b.txt", "BBB");
        var c = temp.WriteText("c.txt", "CCC");
        await Task.CompletedTask;
        return (a, b, c);
    }

    [Fact]
    public async Task Applies_a_plan_in_place()
    {
        using var temp = new TempDir();
        var (a, b, c) = await ThreeFiles(temp);

        var plan = BatchRename.Plan([a, b, c], new RenameSpec { Pattern = "photo_{counter:00}{ext}" });
        var result = BatchRename.Apply(plan);

        Assert.True(result.Success, result.Error);
        Assert.Equal(3, result.Moves.Count);
        Assert.Equal(["photo_01.txt", "photo_02.txt", "photo_03.txt"], Directory.GetFiles(temp.Path).Select(Path.GetFileName).Order());
        Assert.Equal("AAA", File.ReadAllText(temp.Combine("photo_01.txt")));
        Assert.Equal("CCC", File.ReadAllText(temp.Combine("photo_03.txt")));
    }

    [Fact]
    public async Task Refuses_a_plan_with_conflicts_and_changes_nothing()
    {
        using var temp = new TempDir();
        var (a, b, c) = await ThreeFiles(temp);
        temp.WriteText("taken.txt", "keep me");
        var before = Directory.GetFiles(temp.Path).Select(f => (Path.GetFileName(f), File.ReadAllText(f))).Order().ToList();

        var plan = BatchRename.Plan([a, b, c], new RenameSpec { Pattern = "taken{ext}" });
        var result = BatchRename.Apply(plan);

        Assert.False(result.Success);
        Assert.Contains("nothing was changed", result.Error);
        Assert.Equal(before, Directory.GetFiles(temp.Path).Select(f => (Path.GetFileName(f), File.ReadAllText(f))).Order().ToList());
    }

    [Fact]
    public async Task Refuses_when_a_target_appears_after_planning()
    {
        using var temp = new TempDir();
        var (a, b, c) = await ThreeFiles(temp);
        var plan = BatchRename.Plan([a, b, c], new RenameSpec { Pattern = "new_{n}{ext}" });
        temp.WriteText("new_2.txt", "appeared later");

        var result = BatchRename.Apply(plan);

        Assert.False(result.Success);
        Assert.Contains("already exists", result.Error);
        Assert.Equal(["a.txt", "b.txt", "c.txt", "new_2.txt"], Directory.GetFiles(temp.Path).Select(Path.GetFileName).Order());
        Assert.Equal("appeared later", File.ReadAllText(temp.Combine("new_2.txt")));
    }

    [Fact]
    public async Task Refuses_when_a_source_vanished_after_planning()
    {
        using var temp = new TempDir();
        var (a, b, c) = await ThreeFiles(temp);
        var plan = BatchRename.Plan([a, b, c], new RenameSpec { Pattern = "new_{n}{ext}" });
        File.Delete(b);

        var result = BatchRename.Apply(plan);

        Assert.False(result.Success);
        Assert.Contains("no longer exists", result.Error);
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(c));
    }

    [Fact]
    public async Task Chains_and_swaps_work_through_temporary_names()
    {
        using var temp = new TempDir();
        var (a, b, c) = await ThreeFiles(temp);

        // a->b, b->c, c->a : a rotation where every target is another file's current name
        var rotate = new RenamePlan(
        [
            new RenameItem(a, "a.txt", "b.txt", b, RenameIssue.None),
            new RenameItem(b, "b.txt", "c.txt", c, RenameIssue.None),
            new RenameItem(c, "c.txt", "a.txt", a, RenameIssue.None),
        ], [], null);

        var result = BatchRename.Apply(rotate);

        Assert.True(result.Success, result.Error);
        Assert.Equal("CCC", File.ReadAllText(a));
        Assert.Equal("AAA", File.ReadAllText(b));
        Assert.Equal("BBB", File.ReadAllText(c));
        Assert.DoesNotContain(Directory.GetFiles(temp.Path), f => f.Contains(".dropflow-rename-"));
    }

    [Fact]
    public async Task A_case_only_rename_works_on_a_case_insensitive_file_system()
    {
        using var temp = new TempDir();
        var (a, _, _) = await ThreeFiles(temp);

        var plan = BatchRename.Plan([a], new RenameSpec { Case = CaseMode.Upper, LowerCaseExtension = true });
        Assert.False(plan.HasConflicts);

        var result = BatchRename.Apply(plan);

        Assert.True(result.Success, result.Error);
        Assert.Contains("A.txt", Directory.GetFiles(temp.Path).Select(Path.GetFileName));
        Assert.DoesNotContain("a.txt", Directory.GetFiles(temp.Path).Select(Path.GetFileName));
    }

    [RenameFailureFact]
    public async Task A_failure_half_way_rolls_everything_back()
    {
        using var temp = new TempDir();
        var (a, b, c) = await ThreeFiles(temp);

        if (OperatingSystem.IsWindows())
        {
            // b is held open without sharing, so renaming it fails after a was already renamed
            var plan = BatchRename.Plan([a, b, c], new RenameSpec { Pattern = "z_{n}{ext}" });
            using var lockStream = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None);
            var result = BatchRename.Apply(plan);

            Assert.False(result.Success);
            Assert.Contains("Everything was put back", result.Error);
            lockStream.Dispose();
            Assert.Equal(["a.txt", "b.txt", "c.txt"], Directory.GetFiles(temp.Path).Select(Path.GetFileName).Order());
            Assert.Equal("AAA", File.ReadAllText(a));
            return;
        }

        // macOS and Linux rename open files freely, so b lives in a folder that is made read-only after planning: a is renamed,
        // b's rename is refused, and a must be put back.
        var locked = Directory.CreateDirectory(temp.Combine("locked")).FullName;
        var lockedB = Path.Combine(locked, "b.txt");
        File.Move(b, lockedB);
        var unixPlan = BatchRename.Plan([a, lockedB, c], new RenameSpec { Pattern = "z_{n}{ext}" });
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        RenameResult unixResult;
        try
        {
            unixResult = BatchRename.Apply(unixPlan);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.False(unixResult.Success);
        Assert.Contains("Everything was put back", unixResult.Error);
        Assert.Equal(["a.txt", "c.txt"], Directory.GetFiles(temp.Path).Select(Path.GetFileName).Order());
        Assert.True(File.Exists(lockedB));
        Assert.Equal("AAA", File.ReadAllText(a));
    }
    [Fact]
    public async Task Reverse_undoes_a_finished_rename()
    {
        using var temp = new TempDir();
        var (a, b, c) = await ThreeFiles(temp);
        var plan = BatchRename.Plan([a, b, c], new RenameSpec { Pattern = "n{n}{ext}" });
        var done = BatchRename.Apply(plan);
        Assert.True(done.Success, done.Error);

        var undone = BatchRename.Reverse(done.Moves);

        Assert.True(undone.Success, undone.Error);
        Assert.Equal(["a.txt", "b.txt", "c.txt"], Directory.GetFiles(temp.Path).Select(Path.GetFileName).Order());
        Assert.Equal("BBB", File.ReadAllText(b));
    }

    [Fact]
    public async Task Cancellation_rolls_back_too()
    {
        using var temp = new TempDir();
        var (a, b, c) = await ThreeFiles(temp);
        var plan = BatchRename.Plan([a, b, c], new RenameSpec { Pattern = "n{n}{ext}" });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => BatchRename.Apply(plan, cts.Token));
        Assert.Equal(["a.txt", "b.txt", "c.txt"], Directory.GetFiles(temp.Path).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task A_copy_plan_cannot_be_applied_in_place()
    {
        using var temp = new TempDir();
        var (a, _, _) = await ThreeFiles(temp);
        var plan = BatchRename.Plan([a], new RenameSpec { Pattern = "x{ext}" }, new RenamePlanOptions { DestinationDirectory = Path.Combine(temp.Path, "out") });

        var result = BatchRename.Apply(plan);

        Assert.False(result.Success);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public void Folders_can_be_renamed_too()
    {
        using var temp = new TempDir();
        var folder = Kit.NewDir(temp, "old name");
        File.WriteAllText(Path.Combine(folder, "inside.txt"), "x");

        var plan = BatchRename.Plan([folder], new RenameSpec { Pattern = "new name{ext}" });
        var result = BatchRename.Apply(plan);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(temp.Path, "new name", "inside.txt")));
    }
}

public class RenameConverterTests
{
    private static RenameConverter Converter => new();

    [Fact]
    public void Contract_multi_tool_for_any_file_with_options()
    {
        Assert.Equal(["gen.rename"], Converter.Operations);
        Assert.True(Converter.Supports("gen.rename", "png"));
        Assert.True(Converter.Supports("gen.rename", ""));
        Assert.False(Converter.Supports("gen.hash", "png"));
        Assert.Equal(EngineId.None, Converter.EngineFor("gen.rename"));

        var options = Converter.OptionsFor("gen.rename");
        var mode = Assert.Single(options, o => o.Key == "mode");
        Assert.Equal("copy", mode.Default);
        Assert.Equal(["copy", "rename"], mode.Choices);
        Assert.Contains(options, o => o.Key == "pattern");
        Assert.Contains(options, o => o.Key == "find");
        Assert.Contains(options, o => o.Key == "regex");
    }

    [Fact]
    public async Task Default_mode_copies_into_a_new_folder_and_leaves_the_originals_alone()
    {
        using var temp = new TempDir();
        var a = temp.WriteText("b photo.txt", "BBB");
        var b = temp.WriteText("a photo.txt", "AAA");

        var result = await Kit.Run(Converter, Kit.Job("gen.rename", [a, b], new() { ["pattern"] = "trip_{counter:00}{ext}" }));

        Assert.True(result.Success, result.Error);
        var folder = Assert.Single(result.Outputs);
        Assert.Equal("b photo (renamed)", Path.GetFileName(folder));
        // sorted by natural name order: "a photo" is number 1
        Assert.Equal("AAA", File.ReadAllText(Path.Combine(folder, "trip_01.txt")));
        Assert.Equal("BBB", File.ReadAllText(Path.Combine(folder, "trip_02.txt")));
        Assert.Equal("BBB", File.ReadAllText(a));
        Assert.Equal("AAA", File.ReadAllText(b));
        Assert.Equal(["a photo.txt", "b photo (renamed)", "b photo.txt"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Rename_mode_renames_in_place_and_reports_no_deletable_outputs()
    {
        using var temp = new TempDir();
        var a = temp.WriteText("one.txt", "1");
        var b = temp.WriteText("two.txt", "2");

        var result = await Kit.Run(Converter, Kit.Job("gen.rename", [a, b], new() { ["mode"] = "rename", ["pattern"] = "doc_{n}{ext}" }));

        Assert.True(result.Success, result.Error);
        Assert.Empty(result.Outputs);                      // "undo = delete outputs" must never be able to delete user files
        Assert.Contains("Renamed 2 file(s)", result.Note);
        Assert.Equal(["doc_1.txt", "doc_2.txt"], Directory.GetFiles(temp.Path).Select(Path.GetFileName).Order());
        Assert.Equal("1", File.ReadAllText(temp.Combine("doc_1.txt")));
    }

    [Fact]
    public async Task Rename_mode_fails_on_any_conflict_without_touching_anything()
    {
        using var temp = new TempDir();
        var a = temp.WriteText("one.txt", "1");
        var b = temp.WriteText("two.txt", "2");

        var result = await Kit.Run(Converter, Kit.Job("gen.rename", [a, b], new() { ["mode"] = "rename", ["pattern"] = "same{ext}" }));

        Assert.False(result.Success);
        Assert.Contains("nothing was changed", result.Error);
        Assert.Equal(["one.txt", "two.txt"], Directory.GetFiles(temp.Path).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Copy_mode_never_fails_on_duplicate_names_it_numbers_them()
    {
        using var temp = new TempDir();
        var a = temp.WriteText("one.txt", "1");
        var b = temp.WriteText("two.txt", "2");

        var result = await Kit.Run(Converter, Kit.Job("gen.rename", [a, b], new() { ["pattern"] = "same{ext}" }));

        Assert.True(result.Success, result.Error);
        var folder = Assert.Single(result.Outputs);
        Assert.Equal(["same (2).txt", "same.txt"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        Assert.Contains("already taken", result.Note);
    }

    [Fact]
    public async Task Find_replace_regex_case_and_insert_flow_through_the_options()
    {
        using var temp = new TempDir();
        var file = temp.WriteText("IMG_0042_beach.JPG", "x");

        var result = await Kit.Run(Converter, Kit.Job("gen.rename", [file],
            new() { ["find"] = @"IMG_(\d+)_(\w+)", ["replace"] = "$2-$1", ["regex"] = true, ["case"] = "upper", ["insert"] = "trip ", ["lowerExtension"] = true }));

        Assert.True(result.Success, result.Error);
        Assert.Equal(["TRIP BEACH-0042.jpg"], Directory.GetFiles(Assert.Single(result.Outputs)).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Arabic_names_in_and_out()
    {
        using var temp = new TempDir();
        var a = temp.WriteText($"{Kit.ArabicFile} 1.txt", "1");
        var b = temp.WriteText($"{Kit.ArabicFile} 2.txt", "2");

        var result = await Kit.Run(Converter, Kit.Job("gen.rename", [a, b], new() { ["mode"] = "rename", ["pattern"] = "\u0635\u0648\u0631\u0629_{counter:00}{ext}" }));

        Assert.True(result.Success, result.Error);
        Assert.Equal(["\u0635\u0648\u0631\u0629_01.txt", "\u0635\u0648\u0631\u0629_02.txt"], Directory.GetFiles(temp.Path).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Sort_option_none_keeps_the_given_order_for_numbering()
    {
        using var temp = new TempDir();
        var z = temp.WriteText("z.txt", "Z");
        var a = temp.WriteText("a.txt", "A");

        var result = await Kit.Run(Converter, Kit.Job("gen.rename", [z, a], new() { ["pattern"] = "{n}{ext}", ["sort"] = "none" }));

        var folder = Assert.Single(result.Outputs);
        Assert.Equal("Z", File.ReadAllText(Path.Combine(folder, "1.txt")));
        Assert.Equal("A", File.ReadAllText(Path.Combine(folder, "2.txt")));
    }

    [Fact]
    public async Task Bad_patterns_and_modes_fail_with_a_message_and_no_output()
    {
        using var temp = new TempDir();
        var a = temp.WriteText("a.txt", "A");

        var bad = await Kit.Run(Converter, Kit.Job("gen.rename", [a], new() { ["pattern"] = "{nonsense}" }));
        Assert.False(bad.Success);
        Assert.Contains("nonsense", bad.Error);

        var mode = await Kit.Run(Converter, Kit.Job("gen.rename", [a], new() { ["mode"] = "delete" }));
        Assert.False(mode.Success);

        var number = await Kit.Run(Converter, Kit.Job("gen.rename", [a], new() { ["start"] = "many" }));
        Assert.False(number.Success);

        var missing = await Kit.Run(Converter, Kit.Job("gen.rename", [a, temp.Combine("ghost.txt")]));
        Assert.False(missing.Success);

        Assert.Equal(["a.txt"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Copy_mode_honours_the_output_directory_and_refuses_folders()
    {
        using var temp = new TempDir();
        var a = temp.WriteText("a.txt", "A");
        var elsewhere = Path.Combine(temp.Path, "elsewhere");

        var result = await Kit.Run(Converter, Kit.Job("gen.rename", [a], new() { ["pattern"] = "x{ext}" }, elsewhere));
        Assert.Equal(elsewhere, Path.GetDirectoryName(Assert.Single(result.Outputs)));

        var folder = Kit.NewDir(temp, "dir");
        var refused = await Kit.Run(Converter, Kit.Job("gen.rename", [a, folder], new() { ["pattern"] = "x{ext}" }));
        Assert.False(refused.Success);
        Assert.Contains("Folders", refused.Error);
    }

    [Fact]
    public async Task Cancellation_removes_the_partial_copy_folder()
    {
        using var temp = new TempDir();
        // Deterministic on any machine speed: cancel from inside, as soon as the first copy is finished, so the
        // converter has certainly written a partial result folder before it sees the cancelled token.
        var a = temp.WriteText("a.txt", "A");
        var b = temp.WriteText("b.txt", "B");
        using var cts = new CancellationTokenSource();
        var recorder = new Recorder { OnReport = _ => cts.Cancel() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Kit.Run(Converter, Kit.Job("gen.rename", [a, b], new() { ["pattern"] = "x{ext}" }), cts.Token, recorder));

        Assert.NotEmpty(recorder.Items); // the first file really was copied before the cancellation
        Assert.Equal(["a.txt", "b.txt"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Rename_mode_with_nothing_to_change_succeeds_quietly()
    {
        using var temp = new TempDir();
        var a = temp.WriteText("a.txt", "A");

        var result = await Kit.Run(Converter, Kit.Job("gen.rename", [a], new() { ["mode"] = "rename" }));

        Assert.True(result.Success, result.Error);
        Assert.Contains("nothing to rename", result.Note);
        Assert.True(File.Exists(a));
    }
}

/// <summary>
/// The rollback test needs one rename to fail half way: Windows refuses to rename a file held open; elsewhere a read-only folder is
/// used, which root ignores, so the test is skipped (visibly) when it runs as root on macOS/Linux (for example in a container).
/// </summary>
public sealed class RenameFailureFactAttribute : FactAttribute
{
    public RenameFailureFactAttribute()
    {
        if (!OperatingSystem.IsWindows() && Environment.UserName == "root")
            Skip = "Running as root: folder permissions cannot make a rename fail here (run as a normal user, as CI does).";
    }
}