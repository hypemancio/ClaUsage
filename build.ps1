# Builds ClaUsage locally.
#   .\build.ps1                 dist\ClaUsage.exe, small, needs the .NET 10 Desktop Runtime
#   .\build.ps1 -SelfContained  dist\standalone\ClaUsage.exe, compressed, runs on any Windows 10/11 x64
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$sc = if ($SelfContained) { 'true' } else { 'false' }
$out = if ($SelfContained) { "$PSScriptRoot\dist\standalone" } else { "$PSScriptRoot\dist" }

dotnet publish "$PSScriptRoot\src\ClaUsage\ClaUsage.csproj" -c Release -r win-x64 --self-contained $sc `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
  -o $out
# Native commands don't trip $ErrorActionPreference in Windows PowerShell: check the exit code.
if ($LASTEXITCODE -ne 0) { throw "Build failed (is $out\ClaUsage.exe running? Quit it and try again)." }

& "$out\ClaUsage.exe" --selftest | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Self-test failed." }
