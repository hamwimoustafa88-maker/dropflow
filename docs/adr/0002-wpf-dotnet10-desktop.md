# ADR-0002: Desktop app on C# / .NET 10 + WPF

Date: 2026-10-20 · Status: Accepted

## Context
The wheel must be a transparent, borderless, topmost, drop-target window that appears under the cursor over any app,
on Windows 10 and 11, with per-monitor DPI and RTL support.

## Decision
C# on .NET 10 with **WPF** + WPF-UI (Fluent look) + CommunityToolkit.Mvvm. WPF gives full-transparency layered windows
and `AllowDrop` with one flag; WinUI 3 is harder for fully transparent overlays and lacks Windows 10 parity.
Low-level mouse/keyboard hooks run on a dedicated thread (see CLAUDE.md rules). Engines are child processes or isolated
libraries behind `IConverter` so any engine can be replaced without touching the UI.

## Consequences
Windows-only desktop client (the web app, ADR-0004, covers the browser). Requires the .NET 10 SDK on dev machines
(not installed as of 2026-10-20 — see Phase 0).
