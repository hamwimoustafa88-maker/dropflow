using DropFlow.Core.History;
using DropFlow.Core.Operations;
using DropFlow.Core.Settings;
using DropFlow.Core.Wheel;

namespace DropFlow.Core.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var dir = new TempDir();
        var s = new SettingsStore(Path.Combine(dir.Path, "settings.json")).Load();
        Assert.Equal("system", s.Language);
        Assert.Equal(TriggerKey.Shift, s.Keys.Convert);
        Assert.Equal(TriggerKey.Ctrl, s.Keys.Tools);
        Assert.Equal(TriggerKey.Alt, s.Keys.Actions);
    }

    [Fact]
    public void Settings_round_trip_including_enums_as_text()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(Path.Combine(dir.Path, "sub", "settings.json"));
        store.Save(new AppSettings { Language = "ar", Keys = new WheelKeys { Convert = TriggerKey.Alt, Actions = TriggerKey.Shift }, RunAtStartup = true });

        var loaded = store.Load();
        Assert.Equal("ar", loaded.Language);
        Assert.Equal(TriggerKey.Alt, loaded.Keys.Convert);
        Assert.True(loaded.RunAtStartup);
        Assert.Contains("\"Alt\"", File.ReadAllText(store.Path));
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_defaults_and_is_kept_for_inspection()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json", "{ not json");
        var s = new SettingsStore(path).Load();
        Assert.Equal("system", s.Language);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Two_wheels_sharing_a_key_are_reset_to_the_defaults()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        store.Save(new AppSettings { Language = "en", Keys = new WheelKeys { Convert = TriggerKey.Alt, Tools = TriggerKey.Alt, Actions = TriggerKey.Alt } });

        var loaded = store.Load();
        Assert.Equal("en", loaded.Language);
        Assert.True(loaded.Keys.IsValid);
        Assert.Equal(TriggerKey.Shift, loaded.Keys.Convert);
    }

    [Fact]
    public void Saving_leaves_no_temp_file_behind()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        store.Save(new AppSettings());
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public void Pinned_items_default_to_none_and_survive_a_save_and_load()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        Assert.True(store.Load().PinnedItems.IsEmpty);

        var pins = WheelPins.None
            .With(WheelKind.Convert, "image", "img.ocr")
            .With(WheelKind.Convert, "image", "act.copy-path")
            .With(WheelKind.Tools, "pdf", "convert:docx");
        store.Save(new AppSettings { PinnedItems = pins });

        var loaded = store.Load().PinnedItems;
        Assert.Equal(["img.ocr", "act.copy-path"], loaded.For(WheelKind.Convert, "image"));   // order kept
        Assert.Equal(["convert:docx"], loaded.For(WheelKind.Tools, "pdf"));
        Assert.Empty(loaded.For(WheelKind.Convert, "pdf"));
        Assert.Empty(loaded.For(WheelKind.Actions, "image"));
        Assert.Contains("\"PinnedItems\"", File.ReadAllText(store.Path));
        Assert.Contains("\"Convert\"", File.ReadAllText(store.Path));
    }

    [Fact]
    public void Older_files_without_pins_and_hand_edited_pins_load_safely()
    {
        using var dir = new TempDir();
        Assert.True(new SettingsStore(dir.File("old.json", """{ "Language": "ar" }""")).Load().PinnedItems.IsEmpty);
        Assert.True(new SettingsStore(dir.File("null.json", """{ "PinnedItems": null }""")).Load().PinnedItems.IsEmpty);

        var odd = new SettingsStore(dir.File("odd.json",
            """{ "PinnedItems": { "convert": { "image": ["img.ocr", "img.ocr", "", null] }, "Nope": { "image": ["x"] }, "Tools": { "pdf": null } } }""")).Load();
        Assert.Equal(["img.ocr"], odd.PinnedItems.For(WheelKind.Convert, "image"));
        Assert.Equal([new WheelPin(WheelKind.Convert, "image", "img.ocr")], odd.PinnedItems.All);
    }

    [Theory]
    [InlineData("{}", 10)]
    [InlineData("""{ "WheelFrontMax": 8 }""", 8)]
    [InlineData("""{ "WheelFrontMax": 0 }""", 10)]
    [InlineData("""{ "WheelFrontMax": 999 }""", 10)]
    public void The_front_size_of_a_wheel_loads_within_its_limits(string json, int expected)
    {
        using var dir = new TempDir();
        Assert.Equal(expected, new SettingsStore(dir.File("s.json", json)).Load().WheelFrontMax);
    }

    [Fact]
    public void Appearance_values_are_brought_into_range_and_a_bad_accent_is_dropped()
    {
        using var dir = new TempDir();
        var s = new SettingsStore(dir.File("a.json", """{ "WheelOpacity": 0.1, "WheelScale": 9, "LabelScale": -3, "Accent": "purple", "Motion": "Reduce" }""")).Load();
        Assert.Equal(0.6, s.WheelOpacity);
        Assert.Equal(1.4, s.WheelScale);
        Assert.Equal(0.85, s.LabelScale);
        Assert.Null(s.Accent);
        Assert.Equal(MotionSetting.Reduce, s.Motion);
        Assert.Equal("#0A7A5C", (new AppSettings { Accent = "#0A7A5C" }).Normalized().Accent);
    }

    [Fact]
    public void Pins_add_once_and_remove_cleanly()
    {
        var pins = WheelPins.None.With(WheelKind.Actions, "video", "vid.trim");
        Assert.Same(pins, pins.With(WheelKind.Actions, "video", "vid.trim"));          // no duplicates
        Assert.Same(pins, pins.Without(WheelKind.Actions, "audio", "vid.trim"));       // nothing to remove
        Assert.True(pins.Without(WheelKind.Actions, "video", "vid.trim").IsEmpty);
        Assert.True(WheelPins.None.IsEmpty);                                           // the shared empty value is untouched
    }
}

public class HistoryStoreTests
{
    private static ConversionResult Ok(params string[] outputs) => ConversionResult.Ok(outputs);

    [Fact]
    public void Newest_entries_come_first_and_persist()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "history.json");
        var store = new HistoryStore(path);
        store.Add("convert:png", ["a.heic"], Ok("a.png"));
        store.Add("convert:jpg", ["b.heic"], Ok("b.jpg"));

        var reloaded = new HistoryStore(path);
        Assert.Equal(["convert:jpg", "convert:png"], reloaded.Entries.Select(e => e.Operation));
    }

    [Fact]
    public void History_is_capped()
    {
        using var dir = new TempDir();
        var store = new HistoryStore(Path.Combine(dir.Path, "h.json"), maxEntries: 3);
        for (var i = 0; i < 5; i++) store.Add("op" + i, ["x"], Ok());
        Assert.Equal(["op4", "op3", "op2"], store.Entries.Select(e => e.Operation));
    }

    [Fact]
    public void Undo_disposes_existing_outputs_only_and_never_the_inputs()
    {
        using var dir = new TempDir();
        var input = dir.File("a.heic");
        var output = dir.File("a (converted).png");
        var missing = Path.Combine(dir.Path, "gone.png");
        var store = new HistoryStore(Path.Combine(dir.Path, "h.json"));
        var entry = store.Add("convert:png", [input], Ok(output, missing));

        var disposer = new FakeDisposer();
        var removed = store.Undo(entry.Id, disposer);

        Assert.Equal(1, removed);
        Assert.Equal([output], disposer.Disposed);
        Assert.True(store.Entries[0].Undone);
    }

    [Fact]
    public void Undo_twice_does_nothing_the_second_time()
    {
        using var dir = new TempDir();
        var output = dir.File("o.png");
        var store = new HistoryStore(Path.Combine(dir.Path, "h.json"));
        var entry = store.Add("convert:png", ["a"], Ok(output));
        var disposer = new FakeDisposer();

        store.Undo(entry.Id, disposer);
        Assert.Equal(0, store.Undo(entry.Id, disposer));
        Assert.Single(disposer.Disposed);
    }

    [Fact]
    public void Failures_are_recorded_with_their_error()
    {
        using var dir = new TempDir();
        var store = new HistoryStore(Path.Combine(dir.Path, "h.json"));
        store.Add("convert:png", ["a"], ConversionResult.Fail("bad file"));
        var e = store.Entries[0];
        Assert.False(e.Success);
        Assert.Equal("bad file", e.Error);
    }
}

public class SettingsValidatorTests
{
    [Fact]
    public void Defaults_are_valid() => Assert.Empty(SettingsValidator.Validate(new AppSettings()));

    [Fact]
    public void Each_problem_is_reported_with_its_own_code()
    {
        var bad = new AppSettings
        {
            Keys = new WheelKeys { Convert = TriggerKey.Alt, Tools = TriggerKey.Alt, Actions = TriggerKey.Alt },
            GlobalHotkey = "D",
            Language = "xx",
            MaxConcurrency = 0,
        };
        Assert.Equal(["keys.duplicate", "hotkey.invalid", "language.invalid", "concurrency.invalid"], SettingsValidator.Validate(bad));
    }

    [Fact]
    public void The_two_global_shortcuts_must_differ_and_both_be_valid()
    {
        Assert.Equal(["hotkey.duplicate"], SettingsValidator.Validate(new AppSettings { GlobalHotkey = "Ctrl+Alt+D", ClipboardHotkey = "ctrl + alt + d" }));
        Assert.Equal(["hotkey.invalid"], SettingsValidator.Validate(new AppSettings { ClipboardHotkey = "V" }));
    }

    [Theory]
    [InlineData("Ctrl+Shift+F9")]
    [InlineData("Win+Alt+Space")]
    public void Other_good_hotkeys_pass(string hotkey) =>
        Assert.Empty(SettingsValidator.Validate(new AppSettings { GlobalHotkey = hotkey }));
}

public class HistoryOptionsTests
{
    [Fact]
    public void History_keeps_the_options_for_retry_but_never_a_password_or_a_folder()
    {
        using var dir = new TempDir();
        var specs = new[] { new ToolOption("quality", ToolOptionKind.Number), new ToolOption("password", ToolOptionKind.Password), new ToolOption("out", ToolOptionKind.Folder) };
        var values = new Dictionary<string, object?> { ["quality"] = 70, ["password"] = "hunter2", ["out"] = @"D:\secret" };
        var path = System.IO.Path.Combine(dir.Path, "history.json");
        var store = new HistoryStore(path);

        store.Add("img.compress", [@"C:\a.png"], ConversionResult.Ok([@"C:\a (compressed).png"]), HistoryEntry.SafeOptions(specs, values));

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("hunter2", raw);
        Assert.DoesNotContain("secret", raw);
        Assert.Equal(70d, new HistoryStore(path).Entries[0].Options!["quality"]);
        Assert.True(HistoryEntry.NeededSecret(specs));
    }

    [Fact]
    public void Older_history_files_without_options_still_load()
    {
        using var dir = new TempDir();
        var path = dir.File("h.json", """[{"Id":"7d1f2c5e-0000-0000-0000-000000000001","Time":"2026-10-01T10:00:00+00:00","Operation":"convert:jpg","Inputs":["a.png"],"Outputs":["a.jpg"],"Success":true}]""");
        var e = Assert.Single(new HistoryStore(path).Entries);
        Assert.Null(e.Options);
        Assert.Equal("convert:jpg", e.Operation);
    }
}

public class ProxySettingTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("none", true)]
    [InlineData("http://proxy.local:8080", true)]
    [InlineData("socks5://127.0.0.1:1080", true)]
    [InlineData("http://user:pass@proxy:8080", false)]   // no secrets in settings.json
    [InlineData("http://proxy", false)]                  // a port is required
    [InlineData("ftp://proxy:21", false)]
    [InlineData("proxy:8080", false)]
    public void Only_plain_proxy_addresses_are_accepted(string value, bool ok) => Assert.Equal(ok, ProxySetting.IsValid(value));

    [Fact]
    public void Empty_means_the_system_proxy_and_none_means_direct()
    {
        Assert.Null(ProxySetting.Create(""));
        Assert.True(ProxySetting.Create("none")!.IsBypassed(new Uri("https://example.com")));
        Assert.Equal(new Uri("http://proxy.local:8080/"), ProxySetting.Create("http://proxy.local:8080")!.GetProxy(new Uri("https://example.com")));
    }

    [Fact]
    public void A_bad_proxy_or_excluded_apps_list_in_settings_json_is_cleaned()
    {
        var s = new AppSettings { Proxy = "http://u:p@x:1", ExcludedApps = [" photoshop ", "Photoshop", "", "game.exe"] }.Normalized();
        Assert.Equal("", s.Proxy);
        Assert.Equal(["photoshop", "game.exe"], s.ExcludedApps);
    }
}
