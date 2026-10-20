<#
.SYNOPSIS
  Fills installer/winget/DropFlow.yaml for a published GitHub release.
.EXAMPLE
  pwsh installer/winget/make-manifests.ps1 -Version 0.1.0 -RepoUrl https://github.com/OWNER/dropflow
  Downloads the release's Setup.exe, computes its SHA-256 and writes manifests\d\DropFlow\DropFlow\0.1.0\DropFlow.DropFlow.yaml
#>
param(
  [Parameter(Mandatory)] [string] $Version,
  [Parameter(Mandatory)] [string] $RepoUrl
)

$ErrorActionPreference = "Stop"
$installerUrl = "$RepoUrl/releases/download/v$Version/DropFlow-win-Setup.exe"
$tmp = Join-Path $env:TEMP "DropFlow-win-Setup.exe"
Invoke-WebRequest $installerUrl -OutFile $tmp
$sha = (Get-FileHash $tmp -Algorithm SHA256).Hash.ToUpper()

$out = Join-Path (Split-Path -Parent $PSScriptRoot | Split-Path -Parent) "manifests\d\DropFlow\DropFlow\$Version"
New-Item -ItemType Directory -Force $out | Out-Null
(Get-Content (Join-Path $PSScriptRoot "DropFlow.yaml") -Raw).
  Replace("{{VERSION}}", $Version).Replace("{{REPO_URL}}", $RepoUrl).
  Replace("{{INSTALLER_URL}}", $installerUrl).Replace("{{INSTALLER_SHA256}}", $sha) |
  Set-Content (Join-Path $out "DropFlow.DropFlow.yaml") -Encoding utf8

Write-Host "Wrote $out. Validate with: winget validate --manifest $out"
