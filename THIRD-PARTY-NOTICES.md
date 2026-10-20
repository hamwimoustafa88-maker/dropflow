# Third-party notices

Every dependency is recorded here when it is added (see CLAUDE.md, licensing policy). Planned components and their
licenses are listed in `docs/plan.md` §3; this file lists what is **actually used** in the repository.

| Component | Version | License | Used for | Added |
|---|---|---|---|---|
| Next.js | 16.4.2 | MIT | Landing page / web app framework (`web/`) | 2026-10-20 |
| React, React DOM | 19.3.0 | MIT | UI (`web/`) | 2026-10-20 |
| Tailwind CSS (+ @tailwindcss/turbopack) | 4.x | MIT | Styling (`web/`) | 2026-10-20 |
| next-intl | 4.14.9 | MIT | Arabic/English routing and messages (`web/`) | 2026-10-20 |
| TypeScript, ESLint, eslint-config-next, @types/* | 5.x / 9.x | Apache-2.0 / MIT | Dev tooling (`web/`) | 2026-10-20 |
| IBM Plex Sans Arabic, IBM Plex Mono (fonts) | — | SIL OFL 1.1 | Typography, self-hosted at build via `next/font/google` | 2026-10-20 |

Notes
- Fonts are not code; OFL 1.1 permits redistribution with the software.
- `npm audit` (2026-10-20): 0 vulnerabilities in production dependencies; 5 high-severity advisories in the dev-only ESLint chain
  (fast-glob/micromatch) — not shipped, fix when upstream releases a non-breaking update.
