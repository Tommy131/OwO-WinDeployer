#requires -version 5
# WinDeploy 引导脚本 —— 在全新设备上一行启动：
#   irm https://raw.githubusercontent.com/Tommy131/win-provision/main/bootstrap/bootstrap.ps1 | iex
$ErrorActionPreference = 'Stop'

$Root = Join-Path $env:USERPROFILE '.win-provision'
$Repo = 'https://github.com/Tommy131/win-provision.git'

function Have($c) { [bool](Get-Command $c -ErrorAction SilentlyContinue) }

Write-Host '== WinDeploy bootstrap ==' -ForegroundColor Cyan

if (-not (Have 'winget')) {
    Write-Warning 'winget（App Installer）缺失，请先从 Microsoft Store 安装 “App Installer” 后重试。'
}

if (-not (Have 'git')) {
    Write-Host '安装 Git ...' -ForegroundColor Cyan
    winget install --id Git.Git -e --accept-source-agreements --accept-package-agreements --disable-interactivity
}

if (Test-Path (Join-Path $Root '.git')) {
    Write-Host "更新仓库 $Root ..." -ForegroundColor Cyan
    git -C $Root pull --ff-only
} else {
    Write-Host "克隆仓库到 $Root ..." -ForegroundColor Cyan
    git clone --depth 1 $Repo $Root
}

# 现阶段从源码运行（需 .NET SDK）。正式分发将改为下载 Release 中的自包含 WinDeploy.exe。
if (Have 'dotnet') {
    dotnet run --project (Join-Path $Root 'src/WinDeploy.Cli') -- plan --profile dev
    Write-Host ''
    Write-Host '预览完成。开始安装请运行：' -ForegroundColor Green
    Write-Host "  dotnet run --project `"$Root/src/WinDeploy.Cli`" -- apply --profile dev"
} else {
    Write-Warning '未检测到 .NET SDK。正式版会内置自包含 exe；当前可先安装 .NET SDK 后重试。'
}
