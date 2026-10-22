using DropFlow.Desktop.Platform;

namespace DropFlow.Desktop.Tests;

public class LoginItemTests
{
    [Fact]
    public void Launch_agent_opens_the_app_bundle_minimized()
    {
        var plist = LoginItem.LaunchAgentPlist("/Applications/DropFlow.app");
        Assert.Contains("<key>Label</key>\n  <string>dev.scouthub.dropflow</string>", plist);
        Assert.Contains("<string>/usr/bin/open</string>", plist);
        Assert.Contains("<string>/Applications/DropFlow.app</string>", plist);
        Assert.Contains("<string>--minimized</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>\n  <true/>", plist);
        Assert.StartsWith("<?xml", plist);
    }

    [Fact]
    public void Launch_agent_escapes_xml_in_paths()
    {
        var plist = LoginItem.LaunchAgentPlist("/Users/a&b/<Apps>/DropFlow.app");
        Assert.Contains("/Users/a&amp;b/&lt;Apps&gt;/DropFlow.app", plist);
        Assert.DoesNotContain("a&b", plist);
    }

    [Fact]
    public void Autostart_entry_quotes_the_command()
    {
        var entry = LoginItem.AutostartDesktopEntry("/opt/Drop Flow/dropflow-desktop");
        Assert.Contains("[Desktop Entry]", entry);
        Assert.Contains("Type=Application", entry);
        Assert.Contains("Exec=\"/opt/Drop Flow/dropflow-desktop\" --minimized", entry);
        Assert.Contains("X-GNOME-Autostart-enabled=true", entry);
        Assert.Contains("NoDisplay=true", entry);
    }

    [Theory]
    [InlineData("/usr/bin/dropflow-desktop", "/usr/bin/dropflow-desktop")]
    [InlineData("/a b/c", "\"/a b/c\"")]
    [InlineData("/a\"b", "\"/a\\\"b\"")]
    [InlineData("/cost$5", "\"/cost\\$5\"")]
    [InlineData("/x`y", "\"/x\\`y\"")]
    public void Desktop_entry_exec_quoting(string path, string expected) => Assert.Equal(expected, LoginItem.QuoteExec(path));

    [Fact]
    public void The_app_bundle_is_found_from_the_executable_inside_it()
    {
        Assert.Equal("/Applications/DropFlow.app", LoginItem.BundleOf("/Applications/DropFlow.app/Contents/MacOS/dropflow-desktop"));
        Assert.Null(LoginItem.BundleOf("/usr/local/bin/dropflow-desktop"));
    }
}

public class FileManagerEntriesTests
{
    private static readonly AppDirectories Dirs = AppDirectories.For(OsKind.Linux, "/home/u", _ => null);

    [Fact]
    public void Every_supported_file_manager_gets_an_entry_that_opens_the_wheel()
    {
        var files = FileManagerEntries.Plan("/usr/bin/dropflow-desktop", "Open with DropFlow", Dirs, _ => true);
        Assert.Equal(["caja", "dolphin", "nautilus", "nemo"], files.Select(f => f.FileManager).Distinct().Order().ToList());
        Assert.All(files, f => Assert.Contains("--wheel", f.Content));
    }

    [Fact]
    public void Only_installed_file_managers_get_one()
    {
        var files = FileManagerEntries.Plan("/usr/bin/dropflow-desktop", "Open with DropFlow", Dirs, fm => fm == "nautilus");
        var nautilus = Assert.Single(files);
        Assert.Equal("/home/u/.local/share/nautilus/scripts/Open with DropFlow", nautilus.Path);
        Assert.True(nautilus.Executable);
        Assert.StartsWith("#!/bin/sh\n", nautilus.Content);
        Assert.Contains("exec /usr/bin/dropflow-desktop --wheel \"$@\"", nautilus.Content);
    }

    [Fact]
    public void Dolphin_service_menu_is_executable_for_plasma_6_and_passes_all_files()
    {
        var files = FileManagerEntries.Plan("/usr/bin/dropflow-desktop", "Open with DropFlow", Dirs, fm => fm == "dolphin");
        var plasma6 = files.Single(f => f.Path.Contains("/kio/servicemenus/"));
        Assert.True(plasma6.Executable);
        Assert.Contains("Exec=/usr/bin/dropflow-desktop --wheel %F", plasma6.Content);
        Assert.Contains("MimeType=all/all;", plasma6.Content);
    }

    [Fact]
    public void Nemo_action_takes_any_selection()
    {
        var nemo = FileManagerEntries.Plan("/usr/bin/dropflow-desktop", "Open with DropFlow", Dirs, fm => fm == "nemo").Single();
        Assert.Equal("/home/u/.local/share/nemo/actions/dropflow.nemo_action", nemo.Path);
        Assert.Contains("Selection=notnone", nemo.Content);
        Assert.Contains("Extensions=any;", nemo.Content);
    }

    [Fact]
    public void A_shell_script_quotes_an_awkward_command_path()
    {
        var script = FileManagerEntries.ShellScript("/home/u/Apps/Drop Flow's.AppImage");
        Assert.Contains("exec '/home/u/Apps/Drop Flow'\\''s.AppImage' --wheel \"$@\"", script);
    }

    [Fact]
    public void Arabic_titles_are_used_as_file_names_with_slashes_removed()
    {
        var files = FileManagerEntries.Plan("/usr/bin/dropflow-desktop", "افتح بـ DropFlow/x", Dirs, fm => fm == "caja");
        Assert.Equal("/home/u/.config/caja/scripts/افتح بـ DropFlow-x", files.Single().Path);
    }
}

public class GnomeExtensionStateTests
{
    [Theory]
    [InlineData("dropflow@scouthub.dev\n  Name: DropFlow\n  Enabled: Yes\n  State: ACTIVE\n", GnomeExtensionState.Active)]
    [InlineData("dropflow@scouthub.dev\n  Enabled: Yes\n  State: INITIALIZED\n", GnomeExtensionState.NeedsSignOut)]
    [InlineData("dropflow@scouthub.dev\n  Enabled: No\n  State: INACTIVE\n", GnomeExtensionState.Disabled)]
    [InlineData("dropflow@scouthub.dev\n  Enabled: Yes\n  State: ERROR\n", GnomeExtensionState.Error)]
    [InlineData("dropflow@scouthub.dev\n  State: OUT OF DATE\n", GnomeExtensionState.Error)]
    [InlineData("  State: ENABLED\n", GnomeExtensionState.Active)]        // GNOME 45 wording
    [InlineData("  State: DISABLED\n", GnomeExtensionState.Disabled)]
    [InlineData("", GnomeExtensionState.Missing)]
    public void Info_output(string info, GnomeExtensionState expected) => Assert.Equal(expected, GnomeExtension.ParseState(info));

    [Fact]
    public void A_newly_copied_extension_needs_a_sign_out_on_wayland() =>
        Assert.Equal(GnomeExtensionState.NeedsSignOut, GnomeExtension.ParseState(null, filesPresent: true));

    [Fact]
    public void Shell_extension_metadata_matches_what_the_app_installs()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "gnome-extension", GnomeExtension.Uuid);
        var metadata = File.ReadAllText(Path.Combine(dir, "metadata.json"));
        Assert.Contains($"\"uuid\": \"{GnomeExtension.Uuid}\"", metadata);
        foreach (var version in new[] { "45", "46", "47", "48", "49" }) Assert.Contains($"\"{version}\"", metadata);
        var js = File.ReadAllText(Path.Combine(dir, "extension.js"));
        Assert.Contains("'dropflow', 'drag.sock'", js);
        Assert.Contains("get_dnd()", js);
        Assert.Contains("global.get_pointer()", js);
    }
}

public class ShellPlansTests
{
    [Fact]
    public void Mac_reveals_in_finder() =>
        Assert.Equal(new ProcessPlan("open", ["-R", "/Users/a/x.png"]), ShellPlans.Reveal(OsKind.MacOS, "/Users/a/x.png").First());

    [Fact]
    public void Linux_asks_the_file_manager_over_dbus_then_falls_back_to_the_folder()
    {
        var plans = ShellPlans.Reveal(OsKind.Linux, "/home/a/my file.png").ToList();
        Assert.Equal("dbus-send", plans[0].FileName);
        Assert.Contains("array:string:file:///home/a/my%20file.png", plans[0].Arguments);
        Assert.Equal(new ProcessPlan("xdg-open", ["/home/a"]), plans[1]);
    }

    [Fact]
    public void Mac_terminal() =>
        Assert.Equal(new ProcessPlan("open", ["-a", "Terminal", "/Users/a"]), ShellPlans.Terminals(OsKind.MacOS, "/Users/a").First());

    [Fact]
    public void Linux_tries_the_common_terminals_with_their_folder_switches()
    {
        var plans = ShellPlans.Terminals(OsKind.Linux, "/home/a").ToList();
        Assert.Contains(new ProcessPlan("gnome-terminal", ["--working-directory=/home/a"]), plans);
        Assert.Contains(new ProcessPlan("konsole", ["--workdir", "/home/a"]), plans);
        Assert.Contains(new ProcessPlan("ptyxis", ["--working-directory=/home/a"]), plans);
        Assert.Equal("x-terminal-emulator", plans[0].FileName);
    }

    [Fact]
    public void File_urls_percent_encode_unicode_and_reserved_characters() =>
        Assert.Equal("file:///home/a/%D9%85%D9%84%D9%81%20%231.txt", ShellPlans.FileUrl("/home/a/ملف #1.txt"));
}
