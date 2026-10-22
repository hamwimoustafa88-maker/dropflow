<#
.SYNOPSIS
  Downloads the helper programs that ship INSIDE the DropFlow installer, verifies their SHA-256 and unpacks them into
  tools\engines\<Engine>\ (git-ignored; never commit binaries). installer\build.ps1 copies that folder into the app as engines\.
.DESCRIPTION
  End users never download or install anything: FFmpeg (video/audio), qpdf (PDF protect/unlock/repair/compress) and Tesseract
  (OCR, English + Arabic data) are shipped with the app and started as separate programs.
  Licences: qpdf = Apache-2.0, Tesseract = Apache-2.0, tessdata_fast = Apache-2.0,
  FFmpeg (Gyan "essentials" build) = GPL-3.0 -> shipped as a SEPARATE executable (aggregate, never linked) with its LICENSE and a
  source offer; see docs/adr/0012-bundled-engines.md and THIRD-PARTY-NOTICES.md.
  Bumping a version = change the url + sha256 below, then run docs/status.md's engine checks again.
.PARAMETER Arch
  x64 (default, the pins below) or arm64 (Windows on ARM). ARM64 pins: qpdf's official msvc-arm64 build and BtbN's FFmpeg 9.0.2
  winarm64 GPL build (a dated autobuild; BtbN prunes old autobuilds, so if it is gone the script says so and skips FFmpeg).
  Tesseract has no Windows ARM64 build (UB Mannheim ships x64 only), so it is skipped on arm64 unless -AllowX64Emulation is given.
.PARAMETER AllowX64Emulation
  arm64 only: take the x64 build of an engine that has no ARM64 build. Windows 11 on ARM runs it under x64 emulation (slower).
#>
param(
  [string] $OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) "tools\engines"),
  [ValidateSet("x64", "arm64")] [string] $Arch = "x64",
  [switch] $AllowX64Emulation
)

$ErrorActionPreference = "Stop"
$work = Join-Path ([IO.Path]::GetTempPath()) ("dropflow-engines-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work, $OutDir | Out-Null

$pins = @{
  ffmpeg    = @{ url = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip"
                 sha256 = "60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA" }
  qpdf      = @{ url = "https://github.com/qpdf/qpdf/releases/download/v12.4.2/qpdf-12.4.2-msvc64.zip"
                 sha256 = "DB87077E683630C1217E0E8F9A20A9749D952AB676E881C3689187763A5DE25D" }   # matches the official qpdf-12.4.2.sha256
  tesseract = @{ url = "https://github.com/UB-Mannheim/tesseract/releases/download/v5.4.0.20240606/tesseract-ocr-w64-setup-5.4.0.20240606.exe"
                 sha256 = "C885FFF6998E0608BA4BB8AB51436E1C6775C2BAFC2559A19B423E18678B60C9" }
  ara       = @{ url = "https://github.com/tesseract-ocr/tessdata_fast/raw/refs/heads/main/ara.traineddata"
                 sha256 = "E3206D3DC87FD50C24A0FB9F01838615911D25168F4E64415244B67D2BB3E729" }
  eng       = @{ url = "https://github.com/tesseract-ocr/tessdata_fast/raw/refs/heads/main/eng.traineddata"
                 sha256 = "7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2" }
}

# Windows on ARM64. SHA-256 values are the digests GitHub publishes for these release assets (qpdf's also matches its
# official qpdf-12.4.2.sha256). An engine missing here has no native ARM64 build.
$pinsArm64 = @{
  ffmpeg    = @{ url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-21-13-06/ffmpeg-n9.0.2-31-gc89d6ee1ca-winarm64-gpl-9.0.zip"
                 sha256 = "854DB901FF7E5FBF9FF5BAF4FB20846011AAFF9C1D2D1669A45A58E0DBF86EDF"; source = "BtbN/FFmpeg-Builds n9.0.2-31-gc89d6ee1ca winarm64 gpl (GPL-3.0)" }
  qpdf      = @{ url = "https://github.com/qpdf/qpdf/releases/download/v12.4.2/qpdf-12.4.2-msvc-arm64.zip"
                 sha256 = "2B5D7740A2513D8A841116692DC2CF7EFE83F35262386EBAB18BED35FBF15AB1" }
}
$skipped = New-Object System.Collections.Generic.List[string]

# Which pin to use for an engine on this architecture; $null means "skip it" (the reason is recorded).
function Resolve-Pin($name) {
  if ($Arch -eq "x64") { return $pins[$name] }
  if ($pinsArm64.ContainsKey($name)) { return $pinsArm64[$name] }
  if ($name -in "ara", "eng") { return $pins[$name] }          # language data is the same on every architecture
  if ($AllowX64Emulation) { Write-Warning "$name has no Windows ARM64 build: bundling the x64 build (runs under x64 emulation)."; return $pins[$name] }
  $skipped.Add("$name (no pinned Windows ARM64 build; pass -AllowX64Emulation to bundle the x64 build)") | Out-Null
  return $null
}

# Downloads and verifies a pinned file. Returns $null (and records why) when the engine is skipped on this architecture or when
# an ARM64 pin is no longer downloadable; a hash mismatch always stops the script.
function Get-Pinned($name) {
  $pin = Resolve-Pin $name
  if ($null -eq $pin) { return $null }
  $file = Join-Path $work ($name + [IO.Path]::GetExtension(([uri]$pin.url).AbsolutePath))
  Write-Host "Downloading $name ($Arch) ..."
  curl.exe -sL --fail -o $file $pin.url
  if ($LASTEXITCODE -ne 0) {
    if ($Arch -eq "arm64") { $skipped.Add("$name (the pinned ARM64 build is no longer downloadable: $($pin.url); update the pin)") | Out-Null; return $null }
    throw "download failed: $($pin.url)"
  }
  $hash = (Get-FileHash $file -Algorithm SHA256).Hash
  if ($hash -ne $pin.sha256) { throw "SHA-256 mismatch for $name`: expected $($pin.sha256), got $hash" }
  return $file
}

function Find-7z {
  foreach ($c in "7z", "7za") { $cmd = Get-Command $c -ErrorAction SilentlyContinue; if ($cmd) { return $cmd.Source } }
  foreach ($p in "$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe") { if (Test-Path $p) { return $p } }
  throw "7-Zip is needed on the BUILD machine to unpack the Tesseract installer (winget install 7zip.7zip). End users do not need it."
}

try {
  # ---- FFmpeg (separate GPL program) ------------------------------------------------------------------------------
  $zip = Get-Pinned "ffmpeg"
  if ($zip) {
    $ff = Join-Path $OutDir "Ffmpeg"; Remove-Item $ff -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory $ff | Out-Null
    Expand-Archive $zip (Join-Path $work "ffmpeg-x") -Force
    $root = Get-ChildItem (Join-Path $work "ffmpeg-x") -Directory | Select-Object -First 1
    Copy-Item (Join-Path $root.FullName "bin\ffmpeg.exe"), (Join-Path $root.FullName "bin\ffprobe.exe") $ff
    # Gyan names the licence LICENSE, BtbN LICENSE.txt
    $license = @("LICENSE", "LICENSE.txt") | ForEach-Object { Join-Path $root.FullName $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
    Copy-Item $license (Join-Path $ff "LICENSE")
    $build = if ($Arch -eq "arm64" -and $pinsArm64.ContainsKey("ffmpeg")) {
      "BtbN FFmpeg-Builds, $($pinsArm64.ffmpeg.source) - https://github.com/BtbN/FFmpeg-Builds (build scripts in that repository)"
    } else {
      "Gyan.dev `"essentials`" 9.0.2 - https://www.gyan.dev/ffmpeg/builds/ (build scripts: https://github.com/GyanD/codexffmpeg)"
    }
    @"
FFmpeg is shipped with DropFlow as a separate program (ffmpeg.exe / ffprobe.exe); DropFlow starts it as a child process and does not link it.
It is licensed under the GNU GPL v3 (see LICENSE next to this file).
Build ($Arch): $build
Source code of FFmpeg 9.0.2: https://ffmpeg.org/releases/ - and of the libraries built into it (x264, x265, ...): see the README of that build.
You may also request the source from the DropFlow project: https://github.com/hamwimoustafa88-maker/dropflow/issues
"@ | Set-Content (Join-Path $ff "SOURCE-OFFER.txt") -Encoding UTF8
  }

  # ---- qpdf (Apache-2.0) ------------------------------------------------------------------------------------------
  $zip = Get-Pinned "qpdf"
  if ($zip) {
    $q = Join-Path $OutDir "Qpdf"; Remove-Item $q -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory $q | Out-Null
    Expand-Archive $zip (Join-Path $work "qpdf-x") -Force
    $root = Get-ChildItem (Join-Path $work "qpdf-x") -Directory | Select-Object -First 1
    Copy-Item (Join-Path $root.FullName "bin") (Join-Path $q "bin") -Recurse
    foreach ($f in "README.md", "LICENSE.txt", "NOTICE.md") { $p = Join-Path $root.FullName $f; if (Test-Path $p) { Copy-Item $p $q } }
  }

  # ---- Tesseract (Apache-2.0) + English and Arabic data (tessdata_fast, Apache-2.0) ---------------------------------
  $exe = Get-Pinned "tesseract"
  if ($exe) {
    $x = Join-Path $work "tess-x"
    & (Find-7z) x $exe "-o$x" -y | Out-Null
    $t = Join-Path $OutDir "Tesseract"; Remove-Item $t -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory $t | Out-Null
    Copy-Item (Join-Path $x "tesseract.exe") $t
    Get-ChildItem $x -Filter *.dll | Copy-Item -Destination $t
    foreach ($f in "LICENSE", "doc") { $p = Join-Path $x $f; if (Test-Path $p) { Copy-Item $p $t -Recurse } }
    $td = Join-Path $t "tessdata"; New-Item -ItemType Directory $td | Out-Null
    foreach ($sub in "configs", "tessconfigs") { $p = Join-Path $x "tessdata\$sub"; if (Test-Path $p) { Copy-Item $p $td -Recurse } }
    foreach ($f in "pdf.ttf", "osd.traineddata") { $p = Join-Path $x "tessdata\$f"; if (Test-Path $p) { Copy-Item $p $td } }
    Copy-Item (Get-Pinned "eng") (Join-Path $td "eng.traineddata")
    Copy-Item (Get-Pinned "ara") (Join-Path $td "ara.traineddata")
  }

  Write-Host "`nEngines ready in $OutDir ($Arch)"
  Get-ChildItem $OutDir | ForEach-Object { "{0,-10} {1,8:N1} MB" -f $_.Name, ((Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum / 1MB) }
  if ($skipped.Count -gt 0) {
    Write-Warning ("Not bundled for ${Arch}: " + ($skipped -join "; ") + ". The app explains the missing program when a tool needs it.")
    $skipped | Set-Content (Join-Path $OutDir "SKIPPED.txt") -Encoding UTF8
  }
}
finally { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }