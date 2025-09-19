param(
  [Parameter(Mandatory=$true)][string]$RepoPath,
  [Parameter(Mandatory=$true)][string]$Version,
  [Parameter(Mandatory=$true)][string]$MsixPath,
  [string]$AppInstallerRelPath = "deploy/appinstaller/GreyGlass.appinstaller",
  [string]$IndexRelPath = "index.html",
  [string]$DownloadBaseUrl = "https://downloads.hrabnaz.dev",
  [switch]$CommitAndPush
)

$ErrorActionPreference = 'Stop'
Write-Host "=== Publish GreyGlass to GitHub Pages ===" -ForegroundColor Cyan

$repo = Resolve-Path $RepoPath | Select-Object -ExpandProperty Path
$msix = Resolve-Path $MsixPath | Select-Object -ExpandProperty Path
$appInstallerPath = Join-Path $repo $AppInstallerRelPath
$indexPath = Join-Path $repo $IndexRelPath

if (!(Test-Path $repo)) { throw "Repo path not found: $repo" }
if (!(Test-Path $msix)) { throw "MSIX file not found: $msix" }
if (!(Test-Path $appInstallerPath)) { throw "AppInstaller file not found: $appInstallerPath" }
if (!(Test-Path $indexPath)) { throw "index.html not found: $indexPath" }

$arch = 'x64'
if ($msix -match '(x86|x64|arm64)') { $arch = $Matches[1] }

$targetMsixName = "GreyGlass_" + $Version + "_" + $arch + ".msix"
$targetMsixPath = Join-Path $repo $targetMsixName

Write-Host "Copy MSIX => $targetMsixName" -ForegroundColor Yellow
Copy-Item -Force $msix $targetMsixPath

[xml]$xml = Get-Content -Raw $appInstallerPath
if ($xml.AppInstaller -and $xml.AppInstaller.Version) { $xml.AppInstaller.Version = $Version }
$mp = $xml.AppInstaller.MainPackage
if (-not $mp) { throw "MainPackage element not found in AppInstaller." }
$mp.Version = $Version
$mp.Uri = "$DownloadBaseUrl/$targetMsixName"
$xml.Save($appInstallerPath)
Write-Host "Updated AppInstaller: Version=$Version, Uri=$([string]$mp.Uri)" -ForegroundColor Green

$index = Get-Content -Raw $indexPath
$index = [regex]::Replace($index, 'GreyGlass_[0-9A-Za-z\\._-]+\\.msix', [System.Text.RegularExpressions.Regex]::Escape($targetMsixName))
Set-Content -NoNewline -Path $indexPath -Value $index
Write-Host "Updated index.html fallback link -> $targetMsixName" -ForegroundColor Green

if ($CommitAndPush) {
  Push-Location $repo
  git add "$targetMsixName" "$AppInstallerRelPath" "$IndexRelPath" | Out-Null
  git commit -m "chore(release): GreyGlass $Version" | Out-Null
  git push | Out-Null
  Pop-Location
  Write-Host "Pushed to origin." -ForegroundColor Green
}

$installerUrl = "$DownloadBaseUrl/GreyGlass.appinstaller"
$installLink = "ms-appinstaller:?source=$installerUrl"
Write-Host "
Install/Update Link:" -ForegroundColor Cyan
Write-Host $installLink -ForegroundColor White