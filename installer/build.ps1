# Build self-contained Cliplet + Windows Setup.exe
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

Write-Host "==> Publishing self-contained app..." -ForegroundColor Cyan
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish-installer

$iscc = @(
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
  throw "Inno Setup 未找到。请先安装：winget install JRSoftware.InnoSetup"
}

New-Item -ItemType Directory -Force -Path (Join-Path $root "dist") | Out-Null

Write-Host "==> Building installer..." -ForegroundColor Cyan
& $iscc (Join-Path $PSScriptRoot "Cliplet.iss")

$setup = Join-Path $root "dist\Cliplet-Setup.exe"
if (Test-Path $setup) {
  Write-Host ""
  Write-Host "完成：$setup" -ForegroundColor Green
  Write-Host "把这个文件发给别人，双击即可安装。" -ForegroundColor Green
} else {
  throw "未生成 Setup 文件"
}
