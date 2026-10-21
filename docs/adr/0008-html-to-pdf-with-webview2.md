# ADR-0008: HTML to PDF uses the WebView2 runtime that ships with Windows

Date: 2026-10-21 · Status: Accepted

## Decision
`Engines.Web.HtmlToPdfTool` prints local HTML files with `CoreWebView2.PrintToPdfAsync` in one hidden off-screen window.
- **One reused browser.** Creating a new WebView2 right after the previous one was disposed stalled 20-30 s while the old browser
  process shut down (measured: 21/59/61 s for a few tests). The tool keeps one view, reuses it for every job, navigates to
  `about:blank` between jobs and releases it after 30 s idle or on app exit. Tests dropped from about 3 minutes to a few seconds.
- **Network blocked by default.** Every request except `file:`, `data:`, `blob:` and `about:` gets a 403 unless the job option
  `online` is on, so a local page cannot phone home. A test serves a loopback listener and counts connections (0 offline, more than 0 online).
- It also takes over `convert:pdf` for HTML (registered before the LibreOffice converter; a registry test pins that this is the only
  overlap), so HTML to PDF works without LibreOffice.
- The catalog entry belongs to the `document` category (it takes HTML files as input).

## Consequences
Needs the Evergreen WebView2 runtime (preinstalled on Windows 11 and current Windows 10); a clear error is returned if missing.
The tool needs the UI dispatcher, so the CLI does not load it. A page that never finishes loading is cut after 45 s and the browser is discarded.
