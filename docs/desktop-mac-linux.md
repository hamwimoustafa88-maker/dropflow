# DropFlow for macOS and Linux (`src/DropFlow.Desktop`)

A second desktop app, built with **Avalonia 12** (MIT, owner-approved), next to the Windows WPF app (`src/DropFlow.App`, unchanged).
It reuses `DropFlow.Core` (catalog, registry, wheel builder, wheel session, job queue, history, settings) and the portable engine
projects (Images, Media, Pdf, Archive). Executable: `dropflow-desktop`.

## Architecture

```
Program ── single instance (InstanceChannel: lock file + Unix domain socket) ── forward args to the running app and exit
   │
DesktopApp (Avalonia, Fluent theme, no XAML) ── AppController (composition root)
   ├─ Core: ToolCatalog, ToolRegistry(Images, Media, Pdf, Archive), JobQueue, HistoryStore, WheelBuilder, WheelSession
   ├─ IDragWatcher ── MacDragWatcher | X11DragWatcher | ShellDragWatcher (GNOME extension) | NoDragWatcher
   │      └─ DragDecider (pure, tested): rising-edge modifier during an active file drag after > threshold movement
   ├─ WheelWindow (transparent, topmost, borderless, never activates, drop target) + WheelView (draws WheelGeometry)
   ├─ DesktopRunner (options → helper check → JobQueue with the output folder from the settings) + OptionsWindow + ToastWindow
   ├─ Tray / menu-bar icon (Avalonia TrayIcon + NativeMenu): Settings, History, Shelf, How to use, Pause, Quit
   └─ Settings, History, Shelf (drop in, drag out), Welcome windows
```

Pure logic lives in `App/`, `Drag/`, `Platform/`, `Wheel/WheelLogic.cs` and is tested in `tests/DropFlow.Desktop.Tests` (runs on
Windows, macOS and Linux). `WheelGeometry`, `WheelItem`, `OptionForm` and `ShelfModel` are compiled in from `src/DropFlow.Overlay`
(linked files, not copies), so the wheel maths and option validation are identical on every system.

### Drag detection per desktop

| Desktop | How | Permission |
|---|---|---|
| macOS | Poll `NSEvent.pressedMouseButtons`, `modifierFlags`, `mouseLocation` and the drag pasteboard's `changeCount` (snapshot while the button is up) at 60 Hz while the button is down, 5 Hz otherwise; read file URLs from `NSPasteboardNameDrag` (`filePathURL` resolves Finder's file-reference URLs). Objective-C runtime P/Invoke, no binding library | **none** (no Accessibility / Input Monitoring) |
| Linux X11 | Own X connection on a thread: XFixes `SelectionOwnerNotify` on `XdndSelection` = a drag of data started; `XQueryPointer` for buttons, modifiers, position | none |
| GNOME on Wayland | DropFlow's Shell extension `linux/gnome-extension/dropflow@scouthub.dev` (Shell 45–49): `global.backend.get_dnd()` dnd-enter/leave, polls `global.get_pointer()` modifier masks, writes `begin` / `key shift\|ctrl\|alt X Y` / `end` to `$XDG_RUNTIME_DIR/dropflow/drag.sock` (folder 0700, socket 0600). The app copies it to `~/.local/share/gnome-shell/extensions` (unless the package installed it system-wide) and runs `gnome-extensions enable`; GNOME loads it after **one sign-out** (the welcome hint and Settings say so) | none |
| Other Wayland desktops (KDE Plasma, Sway…) | No app can watch drags there. "Open with DropFlow" in Nautilus (Scripts), Caja (Scripts), Nemo (action) and Dolphin (service menu) runs `dropflow-desktop --wheel FILES` → click-mode wheel | none |

Keys: Shift / Ctrl / Alt; on a Mac ⌘ Command fills the Ctrl slot and ⌃ Control the Alt slot (⌥ Option is left to Finder). Users can
reassign them in Settings exactly like on Windows (shared `AppSettings.Keys`).

### The wheel window

* macOS: after creation the NSWindow (from `TryGetPlatformHandle`) gets `setLevel: 101` (pop-up menu), collection behaviour
  canJoinAllSpaces | fullScreenAuxiliary | stationary | ignoresCycle, `hidesOnDeactivate = NO`, and — when Avalonia made an NSPanel —
  the non-activating panel style. It is placed with `setFrameOrigin:` in Cocoa coordinates straight from `NSEvent.mouseLocation` and
  shown with `orderFrontRegardless`.
* X11: override-redirect (mapped at once, above everything, even while a drag holds the pointer), positioned in root pixels, clamped
  to the screen.
* Drops: DropFlow answers **Copy**; Finder offers only Move while ⌘ is held — Move is accepted, but DropFlow only reads the files and
  never moves or deletes the source (`DropEffectPolicy`).

### Where things are kept

| | macOS | Linux |
|---|---|---|
| Settings (`settings.json` shared format, `desktop.json`), history | `~/Library/Application Support/DropFlow` | `$XDG_DATA_HOME/dropflow` |
| Single-instance socket | `~/Library/Application Support/DropFlow/dropflow.sock` | `$XDG_RUNTIME_DIR/dropflow/app.sock` |
| Log (local only) | `~/Library/Logs/DropFlow` | `$XDG_STATE_HOME/dropflow/logs` |
| Start at login | `SMAppService.mainApp` (macOS 13+), fallback `~/Library/LaunchAgents/dev.scouthub.dropflow.plist` | `~/.config/autostart/dropflow.desktop` |
| Undo | moves DropFlow's outputs to `~/.Trash` | freedesktop trash (`$XDG_DATA_HOME/Trash`, with `.trashinfo`) |
| Results | beside the original (default), or Downloads / Desktop (Settings; Linux reads `user-dirs.dirs`, so localised folders work) | same |

Originals are never overwritten or deleted. Network: none. Helper programs are never installed by the app; Settings and the
"helper missing" toast show the command (`brew install qpdf`, `sudo apt install tesseract-ocr tesseract-ocr-ara`, …).

## Packages

| File | Built by | Contents |
|---|---|---|
| `DropFlow-mac-arm64.dmg`, `DropFlow-mac-x64.dmg` | `installer/macos/build-macos.sh` on a Mac | `DropFlow.app` (LSUIElement, `dev.scouthub.dropflow`, en/ar localisations, no `NSAppleEventsUsageDescription` — no Apple events are sent), FFmpeg + ffprobe 9.0.2 in `Contents/Resources/engines`, ad-hoc signed, an `/Applications` link and "Read me first.txt" (en/ar Gatekeeper steps) |
| `DropFlow-linux-amd64.deb`, `-arm64.deb`, `-x86_64.rpm`, `-aarch64.rpm` | `installer/linux/build-linux.sh` (nfpm 2.47.0) | `/usr/lib/dropflow`, `/usr/bin/dropflow-desktop`, desktop entry, icons, the GNOME extension system-wide. Depends on the distribution's `ffmpeg`; recommends `tesseract-ocr` + Arabic, `qpdf`; suggests `libreoffice`. Licence GPL-3.0-or-later |
| `DropFlow-linux-x86_64.AppImage`, `-aarch64.AppImage` | same script (mksquashfs + pinned type2-runtime) | the same tree with FFmpeg inside |

FFmpeg: Martin Riedl's static 9.0.2 release builds (stable versioned URLs, `--enable-gpl --enable-version3`, no non-free), pinned by
SHA-256 in `installer/unix/fetch-ffmpeg.sh`, shipped with the GPL text and a source offer. qpdf and Tesseract have no pinned,
redistributable macOS build → the app shows the Homebrew command.

**No notarization** (owner declined the Apple fee): first launch needs right-click → Open, or System Settings → Privacy & Security →
"Open Anyway". The DMG's "Read me first" and the welcome hint explain it.

### CI

`.github/workflows/build-macos.yml` and `build-linux.yml` are reusable (`workflow_call` with input `version`, also runnable by hand).
They test, build, check the packages, try window snapshots, and upload the artifacts `dropflow-macos` / `dropflow-linux`.
`release.yml` is unchanged; to wire them in, add:

```yaml
  version:
    runs-on: ubuntu-latest
    outputs:
      value: ${{ steps.v.outputs.value }}
    steps:
      - id: v
        run: echo "value=${GITHUB_REF_NAME#v}" >> "$GITHUB_OUTPUT"
  macos:
    needs: version
    uses: ./.github/workflows/build-macos.yml
    with:
      version: ${{ needs.version.outputs.value }}
  linux:
    needs: version
    uses: ./.github/workflows/build-linux.yml
    with:
      version: ${{ needs.version.outputs.value }}
  attach-mac-linux:
    needs: [windows, macos, linux]        # after the Windows job created the release
    runs-on: ubuntu-latest
    steps:
      - uses: actions/download-artifact@v4
        with:
          pattern: dropflow-{macos,linux}
          merge-multiple: true
          path: dist
      - run: gh release upload "${{ github.ref_name }}" dist/* --clobber --repo "${{ github.repository }}"
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

### Build by hand

```bash
dotnet test tests/DropFlow.Desktop.Tests
installer/linux/build-linux.sh 0.2.0 linux-x64            # Linux: needs squashfs-tools, unzip, curl
installer/macos/build-macos.sh 0.2.0 arm64                 # Mac: needs Xcode command-line tools
dotnet run --project src/DropFlow.Desktop -- --snapshot out --lang ar   # renders the windows to PNG and quits
```

Command line: `--wheel [--convert|--tools|--actions] FILES`, `--settings`, `--history`, `--shelf`, `--welcome`, `--quit`,
`--minimized`, `--snapshot DIR [--lang ar|en]`.

## Verified vs. unverified

See the dated entries in `docs/status.md`. In short: the pure logic is unit-tested on Windows and in a Linux container, the app
publishes for linux-x64/arm64 and osx-arm64/x64, the Linux packages were built and inspected in Docker, and the windows were rendered
under Xvfb (en/ar). **Nothing has run on a real Mac**, and no real drag has been tried on X11 or GNOME Wayland: the macOS pasteboard
watcher, NSWindow tweaks, SMAppService, the GNOME extension and the Finder/Nautilus drop paths are written to the documented APIs but
untested. Missing on macOS/Linux for now: AI tools (speech, OCR, background removal), HTML → PDF (WebView2), the Encoder window,
share / send-to-phone actions, and PDF page rendering on macOS (PDFiumCore has no macOS natives).
