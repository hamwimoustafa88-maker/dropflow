# ADR-0015: Tab and the mouse wheel turn the wheel over during a drag (the only input DropFlow swallows)

Date: 2026-11-18 · Status: Accepted (owner: "apply the plan fully"; plan step "wheel back face") · Builds on the Phase 0 hook design

## Context
A wheel with many items (a picture's Tools wheel has 18) is crowded on one ring. The wheel now has a front with the first
`AppSettings.WheelFrontMax` items (10 by default) and a back with the rest and "+ Add". During an OLE file drag the wheel window never
has focus: Windows sends keys and the mouse wheel to the window under the pointer. Until now the low-level hooks only observed input.

## Decision
- `WheelGestureFilter` (DropFlow.Hooks, pure, unit-tested) decides for every key and mouse-wheel event. It swallows input **only** while the
  host has armed it, which `AppHost` does when the wheel is shown in drag mode (with the wheel's screen rectangle). It also disarms when the
  wheel hides and, on the hook thread itself, on mouse-up, so a hung UI thread cannot leave it armed.
- **Swallowed:** a plain Tab or Shift+Tab press during a drag (its key-up too, so the other application never sees half a key press; auto-repeat never
  flips twice), and mouse-wheel turns over the wheel's own rectangle (summed to one 120-unit notch, at most one flip per 250 ms).
- **Never swallowed:** any key with Ctrl, Alt or Win held (Alt+Tab keeps working mid-drag), any other key, and wheel turns outside the wheel.
- In click mode (hotkey, Explorer menu) the wheel has focus: Tab, Shift+Tab, PageUp/PageDown and the mouse wheel are handled by `WheelControl` itself.
- Kill switch: `AppSettings.WheelFlipGestures` (Settings), on by default.

## Consequences
- The hook callback stays O(1) (no allocation beyond the decision struct, no locks), so Windows does not remove the hook.
- Not verifiable from the build machine's shell (no real input): whether the OLE drag loop already consumes Tab, how precision touchpads report
  the wheel, and Windows 11 behaviour. These are rows in `docs/status.md` for the owner to try by hand.
