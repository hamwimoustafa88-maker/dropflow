# DropFlow web

Landing page (`/ar`, `/en`) and, from Phase 6, the in-browser app (`/app`). Next.js 16 + Tailwind 4 + next-intl.

- Dev: `npm run dev` · Build: `npm run build` · Lint: `npm run lint`
- Content: `messages/ar.json` and `messages/en.json` (all UI text). Tool/format data comes from `src/data/catalog.json`,
  **generated** by `node ../catalog/build.mjs` — never edit it by hand.
- Design tokens live in `src/app/globals.css` (`:root` + dark mode). Use tokens, not raw colors.
- Deploy: Vercel with root directory `web/`. The site is fully static; accounts/payments are deferred (ADR-0005).
- Next.js 16 differs from older versions: read `node_modules/next/dist/docs/` (see `AGENTS.md`) before changing framework code.
