# ADR-0003: Drag keys — Shift = Convert, Ctrl = Tools, Alt = Actions

Date: 2026-10-20 · Status: Accepted

## Context
Tangerine uses Shift / Command / Control while dragging. Windows Explorer already gives Shift = move, Ctrl = copy,
Alt = shortcut during a drag.

## Decision
Default mapping: **Shift → conversion wheel, Ctrl → tools wheel (Command's equivalent), Alt → actions wheel.**
The key is detected as a *tap* (press and release) while a drag is in progress; the drop then happens on the wheel, not
on Explorer, so Explorer's modifier semantics never apply. Keys are user-configurable in Settings. Keyboard flow:
Enter on a selected file opens the menu; Ctrl/Alt switch wheels; arrows choose; Enter applies; Esc cancels.
Touch: hold briefly before dragging.

## Consequences
The Phase 0 spike must prove tap detection does not trigger Explorer's move/copy/shortcut behavior or the Alt menu bar.
