# ADR-0013: DropFlow becomes free and open source under GPL-3.0-or-later

Date: 2026-10-21 · Status: Accepted · Supersedes ADR-0010 (proprietary); restores the licence choice of ADR-0001

## Context
The owner decided to open the project to developers, especially in the Arab world, and asked that it "not be used for commercial trade".
A ban on commercial use is incompatible with the Open Source Definition (OSI): licences that forbid it (for example PolyForm
Noncommercial, CC BY-NC) are "source-available", and SignPath Foundation's free code-signing programme accepts only OSI licences without
commercial dual-licensing. Offered the trade-off, the owner chose **GPL-3.0**.

## Decision
- `LICENSE` is the verbatim GNU GPL v3 text; the project is "GPL-3.0-or-later". Copyright (c) 2026 ScoutHub and contributors (developed by the ScoutHub teams; lead developer Moustafa Al-Hamwi).
- **What protects the project from closed commercial exploitation:** the copyleft. Anyone may use DropFlow, including at work, and may even
  sell copies, but whoever distributes a modified version must release its complete source under the GPL. Nobody can turn it into a closed product.
- **Name and logo are not licensed by the GPL** (`TRADEMARK.md`): forks must use another name and logo. This stops look-alike paid copies.
- Contributions are accepted under the same licence (inbound = outbound, Developer Certificate of Origin sign-off; no CLA, so no dual-licensing).
- FFmpeg (GPL) shipped as a separate program is now fully licence-compatible; ADR-0012's legal caveat is resolved. GPL code in-process is
  also allowed from now on (still ask the owner before adding any dependency).
- PDFsharp stays pinned to 6.2.4 (MIT): 6.5+ is AGPL, which would add network obligations; revisit only with the owner.
- The web app may bring back ffmpeg.wasm (GPL-2.0-or-later is compatible with GPL-3.0) if in-browser media is wanted again.

## Consequences
- Eligible to apply to SignPath Foundation for free code signing (see `docs/code-signing-policy.md`).
- All product copy (landing page, terms, Settings, winget manifest, README) says "free and open source, GPL-3.0".
- Earlier proprietary releases: v0.1.0 was published while the licence file said "all rights reserved"; from the next release on, GPL applies.
  The owner, as sole copyright holder at that time, may also state that v0.1.0 is available under the GPL.
