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

## Before announcing
- Run the manual matrix in `docs/phase0-checklist.md` on Windows 10 **and** 11 (drag capture, DPI, elevated apps, antivirus).
- Check `docs/status.md`: anything still marked "unverified" must be either tested or described honestly in the release notes.
- Every dependency must be listed in `THIRD-PARTY-NOTICES.md`; the installer must ship `LICENSE` (GPL-3.0) and the source offer.
