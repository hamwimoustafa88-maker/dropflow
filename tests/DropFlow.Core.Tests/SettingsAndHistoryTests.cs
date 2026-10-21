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
