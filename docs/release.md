# Releasing DropFlow

DropFlow ships as a per-user installer built with [Velopack](https://velopack.io) (no admin rights, silent updates from GitHub
Releases), plus a zipped `dropflow` CLI. Nothing here has been run against a real tag yet — treat the first release as a dry run.

## One-time setup
1. **Make the repository public** (or give the updater a token): the in-app updater reads GitHub Releases anonymously, so a private
   repository gets "no update". Before going public, remove `docs/reference/` (Tangerine's copied site text and screenshots).
2. **Code signing** (recommended; without it Windows SmartScreen shows a blue warning on first run):
   buy an OV/EV code-signing certificate or use Azure Trusted Signing, then add the repository secret `SIGN_PARAMS`
   (the `signtool` arguments, e.g. `/a /f cert.pfx /p <password> /fd sha256 /tr http://timestamp.digicert.com /td sha256`).
   The release workflow passes it to `vpk pack --signParams`.
3. Landing page: replace the inert "Soon" buttons in `web/` with the release URLs, flip `robots` to indexable in
   `web/src/app/[locale]/layout.tsx`, and deploy (Vercel project, root directory `web`).

## Cutting a release
```
git tag v0.1.0
git push origin v0.1.0
```
`.github/workflows/release.yml` then: runs the tests, publishes the app and CLI self-contained for win-x64, builds the installer and
the update feed with `vpk pack`, uploads them with `vpk upload github --publish`, and attaches `dropflow-cli-win-x64.zip` and
`SHA256SUMS.txt`.

Build the same installer locally without publishing: `pwsh installer/build.ps1 -Version 0.1.0` → `Releases\DropFlow-win-Setup.exe`.

## winget
After the GitHub release exists:
```
pwsh installer/winget/make-manifests.ps1 -Version 0.1.0 -RepoUrl https://github.com/<owner>/dropflow
winget validate --manifest manifests\d\DropFlow\DropFlow\0.1.0
```
then open a pull request with that folder against <https://github.com/microsoft/winget-pkgs>. Their bots check the installer and the
SmartScreen reputation; an unsigned installer is often rejected, so sign first.

## What the package contains and needs (check before the first release)
- The app is published **self-contained** (`installer/build.ps1`), so it bundles the .NET desktop runtime **and the ASP.NET Core
  runtime** (the phone-share server uses Kestrel through `Microsoft.AspNetCore.App`; no NuGet package). That makes the installer
  larger — measure it with the first real `vpk pack` and, if it matters, trim or consider a smaller server. A framework-dependent
  install would need the ASP.NET Core 10 runtime on the PC.
- Native libraries that must end up next to the app (verify in `publish/app`): PDFium (`pdfium.dll`), Whisper runtime, ONNX Runtime,
  Magick.NET natives, `WebView2Loader.dll`. PDFiumCore ships win-x64/x86 only, so release win-x64 first.
- **WebView2 runtime** (Evergreen) is preinstalled on Windows 11 and on up-to-date Windows 10; HTML → PDF reports a clear error if it is missing.
- **Windows Firewall**: the first time "Send to phone" listens on the network Windows shows its own allow prompt. If the user cancels it,
  phones cannot connect and the app cannot tell; the share window shows a hint. Not tested with a real phone yet.
- No engines or models are bundled: FFmpeg, qpdf, LibreOffice and 7-Zip are installed through winget with the user's consent, and AI
  models are downloaded only after a confirmation dialog.

## Before announcing
- Run the manual matrix in `docs/phase0-checklist.md` on Windows 10 **and** 11 (drag capture, DPI, elevated apps, antivirus).
- Check `docs/status.md`: anything still marked "unverified" must be either tested or described honestly in the release notes.
- Every dependency must be listed in `THIRD-PARTY-NOTICES.md`; the installer must ship the proprietary `LICENSE`, `THIRD-PARTY-NOTICES.md` and the licence texts/source offers that third-party components require (for example FFmpeg if it is ever bundled).
