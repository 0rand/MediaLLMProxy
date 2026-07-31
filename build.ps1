# MediaLLMProxy build script (Windows PowerShell)
#   .\build.ps1         — restore, build, test
#   .\build.ps1 publish — also publish to dist\ (self-contained, trimmed)
#
# If the .NET SDK is missing, tries `winget install Microsoft.DotNet.SDK.10`
# (Windows 10 1809+/11); pass -SkipSdkInstall to fail with instructions instead.
param([switch]$publish, [switch]$SkipSdkInstall)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  if (-not $SkipSdkInstall) {
    Write-Host "dotnet SDK not found — installing via winget (requires Windows 10 1809+/11)..."
    winget install Microsoft.DotNet.SDK.10 --accept-package-agreements --accept-source-agreements
  }
  if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "dotnet SDK not found. Install .NET 10 SDK: winget install Microsoft.DotNet.SDK.10 (or rerun with -SkipSdkInstall for manual setup)"
    exit 1
  }
}

Write-Host "== restore =="
dotnet restore KineticLLM.sln

Write-Host "== build (Release) =="
dotnet build KineticLLM.sln -c Release --no-restore

Write-Host "== test =="
dotnet test OAIPreRouter.Tester -c Release --no-build

if ($publish) {
  Write-Host "== publish to dist\ =="
  $rid = (dotnet --info | Select-String "RID:").ToString().Split(":")[1].Trim()
  dotnet publish OAIPreRouter.Cli -c Release --no-build `
    -o dist --self-contained true -r $rid `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
  Write-Host "done: dist\"
}
