# ADR-0010: DropFlow is proprietary; dependency policy for a closed-source product

Date: 2026-10-21 · Status: Accepted · Supersedes the license decision of ADR-0001

## Context
ADR-0001 (2026-10-20) chose GPL-3.0-or-later. On 2026-10-21 the owner stated that **DropFlow is not open source**: the repository stays
private and may be made public only briefly for tooling reasons (free GitHub Actions), without any release or announcement.

## Decision
- The `LICENSE` is a proprietary, all-rights-reserved notice. No GPL/AGPL wording remains in user-facing copy (landing page, Settings,
  installer metadata, README).
- **In-process** (linked or bundled into the app) dependencies must be MIT, BSD, Apache-2.0, MPL-2.0, ISC, Zlib, LGPL-2.1+/3.0
  (dynamically linked, replaceable) or similarly permissive. **GPL and AGPL code must not be linked into or shipped inside our
  binaries.** GPL/AGPL programs may only be used as **separate programs the user runs** (FFmpeg, 7-Zip's LGPL, Ghostscript), started as
  child processes, never linked, and never redistributed by us without their license obligations being met.
- Non-commercial or research-only models stay forbidden (for example RMBG-1.4).
- Every dependency is still recorded in `THIRD-PARTY-NOTICES.md`; ask the owner before adding one.
- "No surprises" for users: the app does not offer to download or install helper programs unless the user turns that on (Settings) or
  presses an install button; see ADR-0006 for models.

## Known issue to resolve (needs the owner's decision)
`@ffmpeg/core` (ffmpeg.wasm, GPL-2.0-or-later) is served by the **web app** to browsers and runs in a worker. It talks to our code
only through a message protocol and is a separate module, but shipping GPL code inside a proprietary site carries risk. Options:
(a) keep it, publish its source link and licence text with the site (done in `public/vendor/NOTICE.txt`) and get legal advice;
(b) replace it with an LGPL-only build; (c) drop in-browser audio/video conversion. Until decided, the situation is documented here and in
`docs/deferred.md`.

Bundling the FFmpeg GPL build in the Windows installer (so users never download it) is allowed only as a separate executable with the GPL
text and a source offer; an LGPL build is the safer choice. Also in `docs/deferred.md`.

## Consequences
GPL-compatible tricks (adapting code from File Converter or ShareX, GPL-only libraries) are off the table. The Phase 9 plan no longer has to
"sell services instead of source". The repository must not be made public with a GPL `LICENSE`; with the proprietary `LICENSE`, a
temporary public window shows the code but grants no rights.

## Resolution 2026-10-21
The known issue above is **resolved by option (c)**: `@ffmpeg/core` and `@ffmpeg/ffmpeg` are gone from the web app, so no GPL code is served by the site. FFmpeg now exists only as a separate program shipped in the Windows installer; see ADR-0012 for that decision and its licence note.
