[CmdletBinding()]
param(
  [ValidateSet('Release','Debug')]
  [string]$Configuration = 'Release',

  # 例: win-x64
  [string]$Runtime = 'win-x64',

  [ValidateSet('self-contained','framework-dependent')]
  [string]$Mode = 'self-contained',

  # trueにすると成果物フォルダを事前に掃除します
  [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectPath = Join-Path $RepoRoot 'OrganizerTool\OrganizerTool.csproj'

if (!(Test-Path $ProjectPath)) {
  throw "Project not found: $ProjectPath"
}

$ModeDir = if ($Mode -eq 'self-contained') { 'self-contained' } else { 'framework-dependent' }
$OutDir = Join-Path $RepoRoot ("artifacts\publish\$Runtime\$ModeDir")

if ($Clean -and (Test-Path $OutDir)) {
  Remove-Item $OutDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Write-Host "Publishing..." -ForegroundColor Cyan
Write-Host "  Project: $ProjectPath"
Write-Host "  Config : $Configuration"
Write-Host "  Runtime: $Runtime"
Write-Host "  Mode   : $Mode"
Write-Host "  Output : $OutDir"

if ($Mode -eq 'self-contained') {
  dotnet publish $ProjectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $OutDir `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true
}
else {
  dotnet publish $ProjectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -o $OutDir
}

Write-Host "Done." -ForegroundColor Green
Write-Host "Open: $OutDir" -ForegroundColor Green
