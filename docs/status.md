# Status ledger — what is verified and what is not

Updated by hand whenever a milestone lands. "Verified" means an automated test or a real run on this machine
(Windows 10 Pro 19045) proved it. Anything else is "built, unverified".

| Area | State | Evidence / gap |
|---|---|---|
| Catalog + Tangerine parity | Verified | `node catalog/build.mjs` |
| Landing page (ar/en, dark/light, mobile) | Verified | headless-Edge e2e (16 checks), screenshots |
| Core (catalog, registry, queue, wheel model, settings, history) | Verified | `tests/DropFlow.Core.Tests` |
| Engines.Common (process runner, locator, winget command) | Verified | `tests/DropFlow.Engines.Common.Tests` (winget itself never run) |
| Drag + key-tap state machine | Verified | `tests/DropFlow.Hooks.Tests` |
| Real mouse/keyboard hooks, wheel as OLE drop target | **Unverified** | Phase 0 manual checklist not run |
| Windows 11, multi-monitor, per-monitor DPI | **Unverified** | needs hardware |

(Engine projects, UI, installer, web app: see the sections appended below as they land.)
