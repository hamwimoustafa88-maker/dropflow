<#
.SYNOPSIS
  Builds a local DropFlow installer (Velopack) without publishing anything.
.EXAMPLE
  pwsh installer/build.ps1 -Version 0.1.0
  Output: Releases\DropFlow-win-Setup.exe and the update feed files.
#>
param(
  [Parameter(Mandatory)] [string] $Version,
  [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
  Write-Host "Installing Velopack's packer (dotnet tool)..."
  dotnet tool install -g vpk
}

Remove-Item publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish src/DropFlow.App -c $Configuration -r win-x64 --self-contained "-p:Version=$Version" -o publish/app
dotnet publish src/DropFlow.Cli -c $Configuration -r win-x64 --self-contained "-p:Version=$Version" -o publish/cli

vpk pack --packId DropFlow --packTitle DropFlow --packVersion $Version `
  --packDir publish/app --mainExe DropFlow.exe --icon src/DropFlow.App/Assets/dropflow.ico

Write-Host "`nDone. Installer: Releases\DropFlow-win-Setup.exe"
Write-Host "Unsigned builds trigger a Windows SmartScreen warning; see docs/release.md for signing."
