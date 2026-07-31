# MediaLLMProxy build script (Windows PowerShell)
#   .\build.ps1         — restore, build, test
#   .\build.ps1 publish — also publish to dist\ (self-contained, trimmed)
param([switch]$publish)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  Write-Error "dotnet SDK not found. Install .NET 10 SDK: https://dotnet.microsoft.com/download"
  exit 1
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
