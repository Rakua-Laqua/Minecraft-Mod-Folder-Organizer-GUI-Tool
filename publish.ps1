[CmdletBinding()]
param(
  [ValidateSet('Release','Debug')]
  [string]$Configuration = 'Release',

  # 例: win-x64
  [string]$Runtime = 'win-x64',

  [ValidateSet('self-contained','framework-dependent','both')]
  [string]$Mode = 'self-contained',

  # trueにすると成果物フォルダを事前に掃除します
  [switch]$Clean,

  # trueにすると publish 出力をzip化します（配布用）
  [switch]$Zip
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectPath = Join-Path $RepoRoot 'OrganizerTool\OrganizerTool.csproj'

if (!(Test-Path $ProjectPath)) {
  throw "Project not found: $ProjectPath"
}

function Get-AppVersion {
  param(
    [Parameter(Mandatory=$true)][string]$CsprojPath
  )

  # 1) csproj の <Version> を優先
  try {
    [xml]$xml = Get-Content -LiteralPath $CsprojPath -Raw
    $v = $xml.Project.PropertyGroup.Version | Select-Object -First 1
    if (![string]::IsNullOrWhiteSpace($v)) {
      return $v.Trim()
    }
  }
  catch {
    # ignore
  }

  # 2) git の短縮ハッシュ（利用できる場合）
  try {
    $git = Get-Command git -ErrorAction Stop
    $hash = (& $git.Source -C $RepoRoot rev-parse --short HEAD) 2>$null
    if (![string]::IsNullOrWhiteSpace($hash)) {
      return $hash.Trim()
    }
  }
  catch {
    # ignore
  }

  return ""
}

$AppName = "OrganizerTool"
$AppVersion = Get-AppVersion -CsprojPath $ProjectPath
$DistDir = Join-Path $RepoRoot ("artifacts\dist\$Runtime")

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

  if ($Zip) {
    New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

    $verPart = if (![string]::IsNullOrWhiteSpace($AppVersion)) { "-v$AppVersion" } else { "" }
    $zipName = "$AppName$verPart-$Runtime-$OneMode.zip"
    $zipPath = Join-Path $DistDir $zipName

    if (Test-Path $zipPath) {
      Remove-Item $zipPath -Force
    }

    Write-Host "Zipping..." -ForegroundColor Cyan
    Write-Host "  Source: $OutDir"
    Write-Host "  Output: $zipPath"

    Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $zipPath -Force
    Write-Host "Zip done." -ForegroundColor Green
  }
}

if ($Mode -eq 'both') {
  Publish-One -OneMode 'self-contained'
  Publish-One -OneMode 'framework-dependent'
}
else {
  Publish-One -OneMode $Mode
}
