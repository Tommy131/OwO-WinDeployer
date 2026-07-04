#requires -version 5
# 产出自包含单文件可执行（目标机无需安装 .NET 运行时）。
#   pwsh -File scripts/publish.ps1
param(
    [string]$Runtime = 'win-x64',
    [string]$Config = 'Release'
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts'
$common = @('-c', $Config, '-r', $Runtime, '--self-contained', 'true', '-p:PublishSingleFile=true', '--nologo')

Write-Host '== 发布 OwOWinDeployer.App (GUI) ==' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src/OwOWinDeployer.App/OwOWinDeployer.App.csproj') @common `
    '-p:IncludeNativeLibrariesForSelfExtract=true' -o (Join-Path $out 'app')

Write-Host '== 发布 OwOWinDeployer.Cli ==' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src/OwOWinDeployer.Cli/OwOWinDeployer.Cli.csproj') @common -o (Join-Path $out 'cli')

# CLI 别名：同一可执行文件的副本，让用户可用更短的 `owodeploy` 调用（单文件自包含，副本可独立运行）。
Copy-Item (Join-Path $out 'cli/owowindeployer.exe') (Join-Path $out 'cli/owodeploy.exe') -Force

Write-Host '完成。产物:' -ForegroundColor Green
Get-ChildItem -Recurse $out -Filter *.exe | ForEach-Object { '{0,-12} {1,7:N1} MB' -f $_.Name, ($_.Length / 1MB) }
