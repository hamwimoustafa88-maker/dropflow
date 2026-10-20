# Phase 0 spike — manual test checklist

Goal: prove that DropFlow can detect "drag + modifier tap" anywhere in Windows and show a drop-target wheel under the
cursor. **Do not start Phase 1 until every "must" row passes on both Windows 10 and Windows 11.** Record results in the
table; paste the spike's *Copy log* output for any failure.

## Run
```
dotnet run --project src/DropFlow.Spike
```
Keep the spike window open (it can be behind other windows). Drag a file, tap Shift (then Ctrl, then Alt), drop on the wheel.

## Automated (already green)
- `dotnet test` — 16 tests for the drag / key-tap state machine (`tests/DropFlow.Hooks.Tests`).
- Spike builds with 0 warnings and starts, stays responsive and installs hooks (checked on Windows 10 Pro 19045).

## Manual matrix
Legend: **must** = blocks Phase 1 · note what you see in the last column.

| # | Scenario | Windows 10 | Windows 11 | Must | Notes |
|---|---|---|---|---|---|
| 1 | Drag a file from Explorer, tap **Shift** → wheel appears centred under cursor | ☐ | ☐ | must | |
| 2 | Same with **Ctrl** → "Tools" wheel | ☐ | ☐ | must | |
| 3 | Same with **Alt** → "Actions" wheel; Explorer's/app's Alt menu bar does NOT activate | ☐ | ☐ | must | |
| 4 | Drop on the wheel → paths logged, **no** move/copy/shortcut happens in Explorer | ☐ | ☐ | must | |
| 5 | Drop elsewhere / release outside wheel → wheel hides within ~0.5 s | ☐ | ☐ | must | |
| 6 | Press **Esc** while dragging → wheel hides, drag cancelled | ☐ | ☐ | must | |
| 7 | Drag from the **desktop** | ☐ | ☐ | must | |
| 8 | Drag a **browser download** (Chrome/Edge download bar → wheel) | ☐ | ☐ | must | |
| 9 | Drag **several files** and a **folder** → all paths logged | ☐ | ☐ | must | |
| 10 | Wheel never steals focus (the source window stays active, drag continues) | ☐ | ☐ | must | |
| 11 | Two monitors, **different DPI** (e.g. 100 % + 150 %): wheel centred and sized correctly on each | ☐ | ☐ | must | log shows dpi + rect |
| 12 | Drag across monitors then tap key → wheel on the monitor under the cursor | ☐ | ☐ | must | |
| 13 | Drag a file out of an **elevated** app → expected to fail; confirm and document the behaviour | ☐ | ☐ | doc | |
| 14 | **Touch / pen**: long-press then drag, tap a modifier on the on-screen keyboard | ☐ | ☐ | should | |
| 15 | Antivirus (Defender + one third party): no block or warning for the hooks | ☐ | ☐ | should | |
| 16 | CPU while idle with hooks installed stays ≈ 0 %; no input lag while typing/mouse | ☐ | ☐ | must | |
| 17 | Keys held *before* the drag (e.g. Shift held, then drag) do not trigger the wheel | ☐ | ☐ | should | |

## Decisions the spike must produce
1. Does tapping Shift/Ctrl/Alt mid-drag change Explorer's drop effect or flash menus? If yes → enable swallowing the key
   events while `IsDragging` (`LowLevelInputHook` currently only observes).
2. Is `#01000000` enough for the layered window to receive OLE drops on Windows 10 and 11?
3. Does `SetWindowPos` + WPF per-monitor DPI handling place the wheel correctly across DPI boundaries, or do we need to
   handle `WM_DPICHANGED` manually?
4. Is a tap of arbitrary duration acceptable, or do we need a maximum tap time?

Fill the answers into a new ADR (`docs/adr/0006-input-capture.md`) when done.
