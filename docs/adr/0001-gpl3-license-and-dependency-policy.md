# ADR-0001: GPL-3.0-or-later and dependency policy

Date: 2026-10-20 · Status: **Superseded by [ADR-0010](0010-proprietary-license-and-dependency-policy.md)** (the owner decided on 2026-10-21 that DropFlow is not open source)

## Context
The project owner chose a fully open-source model. This lets us bundle the full FFmpeg GPL build (x264/x265) and
adapt code from GPL projects (File Converter, ShareX), and removes the earlier "keep it sellable" constraint.

## Decision
- DropFlow is licensed **GPL-3.0-or-later** (see `LICENSE`).
- Allowed in-process: MIT, BSD, Apache-2.0, LGPL, MPL-2.0, GPL-2.0-or-later, GPL-3.0.
- AGPL-3.0 tools (Ghostscript, MuPDF) only as **separate processes or WASM modules**, never linked.
- Forbidden: GPL-2.0-only code in-process (incompatible with GPLv3), non-commercial/research-only models
  (e.g. RMBG-1.4), anything not redistributable.
- Code adapted from other GPL projects keeps its copyright header; all third-party components are listed in
  `THIRD-PARTY-NOTICES.md`. The user must be asked before any new dependency is added.

## Consequences
- Anyone may redistribute the code; future paid plans (Phase 9) must charge for services (sync, signed builds,
  support, optional cloud features), not for withholding source.
- Releases must ship corresponding source and the license text.
