// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Desktop.Drag;
using DropFlow.Desktop.Platform;

namespace DropFlow.Desktop.App;

/// <summary>Which paragraphs the welcome hint shows on this desktop (string keys, in order).</summary>
public static class WelcomeText
{
    public static IReadOnlyList<string> Keys(HostSession session, GnomeExtensionState extension)
    {
        var keys = new List<string>();
        if (session.IsMac)
        {
            keys.AddRange(["welcome.drag.mac", "welcome.mac.menubar", "welcome.mac.gatekeeper", "welcome.privacy"]);
            return keys;
        }

        switch (DragWatcherFactory.Choose(session))
        {
            case DragWatcherKind.GnomeShell:
                keys.Add("welcome.drag.linux");
                if (extension == GnomeExtensionState.NeedsSignOut) keys.Add("welcome.gnome.signout");
                else if (extension is GnomeExtensionState.Error or GnomeExtensionState.Disabled or GnomeExtensionState.Missing) keys.Add("welcome.gnome.error");
                keys.Add("welcome.clickAlso");
                break;
            case DragWatcherKind.X11:
                keys.Add("welcome.drag.linux");
                keys.Add("welcome.clickAlso");
                break;
            default:
                keys.Add("welcome.click");
                break;
        }
        keys.Add("welcome.linux.tray");
        keys.Add("welcome.privacy");
        return keys;
    }
}
