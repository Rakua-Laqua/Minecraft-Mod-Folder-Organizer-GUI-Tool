[CmdletBinding()]
param(
  [ValidateSet('Release','Debug')]
  [string]$Configuration = 'Release',

  # 例: win-x64
  [string]$Runtime = 'win-x64',

  [ValidateSet('self-contained','framework-dependent','both')]
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

function Publish-One {
  param(
    [Parameter(Mandatory=$true)][ValidateSet('self-contained','framework-dependent')][string]$OneMode
  )

  $ModeDir = if ($OneMode -eq 'self-contained') { 'self-contained' } else { 'framework-dependent' }
  $OutDir = Join-Path $RepoRoot ("artifacts\publish\$Runtime\$ModeDir")

  if ($Clean -and (Test-Path $OutDir)) {
    Remove-Item $OutDir -Recurse -Force
  }

  New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

  Write-Host "Publishing..." -ForegroundColor Cyan
  Write-Host "  Project: $ProjectPath"
  Write-Host "  Config : $Configuration"
  Write-Host "  Runtime: $Runtime"
  Write-Host "  Mode   : $OneMode"
  Write-Host "  Output : $OutDir"

  if ($OneMode -eq 'self-contained') {
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
}

if ($Mode -eq 'both') {
  Publish-One -OneMode 'self-contained'
  Publish-One -OneMode 'framework-dependent'
}
else {
  Publish-One -OneMode $Mode
}
