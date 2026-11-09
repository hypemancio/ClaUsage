# Builds dist\ClaUsage.exe.
#   .\build.ps1                 small exe, needs the .NET 10 Desktop Runtime
#   .\build.ps1 -SelfContained  bigger exe that runs on any Windows 10/11 x64
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$sc = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish "$PSScriptRoot\src\ClaUsage\ClaUsage.csproj" -c Release -r win-x64 --self-contained $sc `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
  -o "$PSScriptRoot\dist"
& "$PSScriptRoot\dist\ClaUsage.exe" --selftest | Out-Host
