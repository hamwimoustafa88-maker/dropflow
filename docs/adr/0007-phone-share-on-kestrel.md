# ADR-0007: "Send to phone" is served by Kestrel, not HttpListener

Date: 2026-10-21 · Status: Accepted

## Context
The first version used `HttpListener`. On Windows it refuses to listen on a non-loopback address without an elevated URL
reservation (`netsh http add urlacl`), even for an administrator account running a non-elevated process. DropFlow never runs
elevated (see CLAUDE.md), so the share silently fell back to `127.0.0.1`, which a phone cannot reach.

## Decision
`PhoneShareServer` runs on Kestrel from the .NET shared framework (`FrameworkReference Microsoft.AspNetCore.App`; no NuGet package).
One plain-HTTP/1.1 endpoint only: `0.0.0.0:<free port>` in LAN mode, loopback otherwise. Everything lives under an unguessable
128-bit token path; anything else gets an identical 404; only the files given to the constructor are ever readable (paths are never
taken from the request). Kestrel limits are set explicitly (request line/headers/body size, header and data-rate timeouts,
32 connections, no `Server` header). The share has a lifetime (15 minutes in the app), supports single-download mode, and
closing the window or stopping frees the port at once.

## Consequences
- Sharing on the network needs no administrator rights. Windows Firewall still shows its own one-time prompt; if the user cancels it,
  phones cannot connect and the app cannot tell, so the window shows a hint.
- The installer grows because a self-contained package now bundles the ASP.NET Core runtime (to be measured at the first `vpk pack`).
- IPv4 only; a phone on another subnet, AP isolation and VPNs are not handled.
