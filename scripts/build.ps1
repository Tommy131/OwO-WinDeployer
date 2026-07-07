#requires -version 5
# 本地一键构建：像 CI(.github/workflows/release.yml)一样，在本地同时产出三种发行形态，
# 全程本地执行、不经过 CICD。版本号取自根 Directory.Build.props（单一来源），无需改本脚本。
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1                 # 三形态全打包 -> build\
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Run            # 构建后启动单文件版
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Variant singlefile
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Config Debug
#
# 三种形态（均自带 catalog/ configs/ assets/ tools/ 数据链，含 GUI + CLI）：
#   with-runtime —— 集成 .NET 运行环境：自包含、多文件，目标机免装 .NET。
#   framework    —— 纯 App（仅程序）：依赖框架、体积最小，需目标机装 .NET 10 桌面运行时。
#   singlefile   —— 单文件：自包含、单个 OwOWinDeployer.exe，目标机免装 .NET。
param(
    [string]$Runtime = 'win-x64',
    [string]$Config  = 'Release',
    [ValidateSet('all', 'with-runtime', 'framework', 'singlefile')]
    [string]$Variant,
    [switch]$Run
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$app  = Join-Path $root 'src\OwOWinDeployer.App\OwOWinDeployer.App.csproj'
$cli  = Join-Path $root 'src\OwOWinDeployer.Cli\OwOWinDeployer.Cli.csproj'
$dist = Join-Path $root 'build'

# 结束正在运行的实例，避免单文件 exe 被占用导致清理/发布失败。
Get-Process OwOWinDeployer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 读取唯一版本源（仅用于命名与显示）。
$ver = '0.0.0'
$props = Join-Path $root 'Directory.Build.props'
if (Test-Path $props) {
    $m = [regex]::Match((Get-Content $props -Raw -Encoding UTF8), '<Version>\s*([^<\s]+)\s*</Version>')
    if ($m.Success) { $ver = $m.Groups[1].Value }
}

# 三形态定义：Sc = --self-contained，Single = -p:PublishSingleFile（与 release.yml 完全一致）。
$all = @(
    @{ Key = 'with-runtime'; Sc = 'true';  Single = 'false' },
    @{ Key = 'framework';    Sc = 'false'; Single = 'false' },
    @{ Key = 'singlefile';   Sc = 'true';  Single = 'true'  }
)
# 选择形态：显式传了 -Variant 就用它（可脚本化/非交互）；否则弹菜单让用户选，默认仅 framework（纯 App 版）。
function Read-VariantChoice {
    Write-Host ''
    Write-Host '请选择要构建的形态（可多选，用逗号分隔；直接回车 = 默认仅 1）：' -ForegroundColor Cyan
    Write-Host '  1. framework     纯 App —— 依赖框架、体积最小，需目标机装 .NET 10 桌面运行时  [默认]'
    Write-Host '  2. singlefile    单文件 —— 自包含单个 OwOWinDeployer.exe，目标机免装 .NET'
    Write-Host '  3. with-runtime  集成运行环境 —— 自包含、多文件，目标机免装 .NET'
    Write-Host '  4. all           以上全部三种'
    $ans = Read-Host '输入编号'
    if ([string]::IsNullOrWhiteSpace($ans)) { return @('framework') }
    if ($ans -match '(^|[,\s])4([,\s]|$)') { return @('with-runtime', 'framework', 'singlefile') }
    $map = @{ '1' = 'framework'; '2' = 'singlefile'; '3' = 'with-runtime' }
    $keys = @()
    foreach ($n in ($ans -split '[,\s]+' | Where-Object { $_ })) {
        if ($map.ContainsKey($n)) { $keys += $map[$n] } else { Write-Host "忽略无效选项: $n" -ForegroundColor Yellow }
    }
    if (-not $keys) { $keys = @('framework') }
    return ($keys | Select-Object -Unique)
}

if ($PSBoundParameters.ContainsKey('Variant')) {
    $selectedKeys = if ($Variant -eq 'all') { $all | ForEach-Object { $_.Key } } else { @($Variant) }
}
else {
    $selectedKeys = Read-VariantChoice
}
# 按 $all 的固定顺序（with-runtime → framework → singlefile）过滤出所选形态。
$variants = $all | Where-Object { $selectedKeys -contains $_.Key }

Write-Host "== 构建 OwOWinDeployer v$ver ($Config / $Runtime) -> build\ ==" -ForegroundColor Cyan
Write-Host ("形态: " + (($variants | ForEach-Object { $_.Key }) -join ', ')) -ForegroundColor Cyan

# 清空旧输出。尽力而为：管理员运行遗留的内核硬件监控驱动（WinRing0）可能锁住文件，跳过即可，发布会刷新 exe。
if (Test-Path $dist) { Remove-Item (Join-Path $dist '*') -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# 单文件形态会把可直接运行的 GUI 复制到这里（清理中间目录后仍保留）。
$rootExe = Join-Path $dist 'OwOWinDeployer.exe'

function Invoke-Publish($proj, $v, $outDir) {
    $pubArgs = @('-c', $Config, '-r', $Runtime, '--self-contained', $v.Sc,
                 "-p:PublishSingleFile=$($v.Single)", '-p:DebugType=none', '--nologo', '-o', $outDir)
    if ($v.Single -eq 'true') { $pubArgs += '-p:IncludeNativeLibrariesForSelfExtract=true' }
    dotnet publish $proj @pubArgs
    if ($LASTEXITCODE -ne 0) { throw "发布失败: $proj [$($v.Key)]" }
}

foreach ($v in $variants) {
    $appOut = Join-Path $dist "$($v.Key)\app"
    $cliOut = Join-Path $dist "$($v.Key)\cli"

    Write-Host "== 发布 [$($v.Key)] GUI ==" -ForegroundColor Cyan
    Invoke-Publish $app $v $appOut
    Write-Host "== 发布 [$($v.Key)] CLI ==" -ForegroundColor Cyan
    Invoke-Publish $cli $v $cliOut

    # 组装发行包：运行所需数据放在 exe 同级（程序向上查找 catalog/，再据此定位 repoRoot/assets）。
    $name  = "OwO-Win-Deployer-v$ver-$Runtime-$($v.Key)"
    $stage = Join-Path $dist $name
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'assets') | Out-Null

    if ($v.Single -eq 'true') {
        # 单文件：仅一个 GUI exe（运行库已并入）+ CLI 单文件及其短别名。
        Copy-Item (Join-Path $appOut 'OwOWinDeployer.exe') (Join-Path $stage 'OwOWinDeployer.exe')     -Force
        Copy-Item (Join-Path $cliOut 'owowindeployer.exe') (Join-Path $stage 'owowindeployer-cli.exe') -Force
        Copy-Item (Join-Path $cliOut 'owowindeployer.exe') (Join-Path $stage 'owodeploy.exe')          -Force
    }
    else {
        # 多文件：GUI 全量输出放根目录；CLI 放 cli\ 子目录（两者运行库 DLL 集不同，分目录避免冲突）。
        Copy-Item (Join-Path $appOut '*') $stage -Recurse -Force
        $cliDir = Join-Path $stage 'cli'
        New-Item -ItemType Directory -Force -Path $cliDir | Out-Null
        Copy-Item (Join-Path $cliOut '*') $cliDir -Recurse -Force
        Copy-Item (Join-Path $cliDir 'owowindeployer.exe') (Join-Path $cliDir 'owodeploy.exe') -Force
    }

    Copy-Item (Join-Path $root 'catalog')      (Join-Path $stage 'catalog')      -Recurse -Force
    Copy-Item (Join-Path $root 'assets\icons') (Join-Path $stage 'assets\icons') -Recurse -Force
    Copy-Item (Join-Path $root 'configs')      (Join-Path $stage 'configs')      -Recurse -Force
    Copy-Item (Join-Path $root 'tools')        (Join-Path $stage 'tools')        -Recurse -Force   # 随附 smartctl.exe（USB SMART）
    Copy-Item (Join-Path $root 'LICENSE')      (Join-Path $stage 'LICENSE')      -Force
    Copy-Item (Join-Path $root 'README.md')    (Join-Path $stage 'README.md')    -Force

    $zip = Join-Path $dist "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $stage -DestinationPath $zip -Force
    Write-Host "已打包: $zip" -ForegroundColor Green

    # 单文件形态：先把可直接运行的 exe 留到 build\ 根目录，再清理中间目录。
    if ($v.Single -eq 'true') { Copy-Item (Join-Path $appOut 'OwOWinDeployer.exe') $rootExe -Force }

    # 打包完成即删除原始发布目录（build\<key>\）与暂存目录，只保留 zip，避免构建残留占用空间。
    Remove-Item (Join-Path $dist $v.Key) -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host '完成。产物:' -ForegroundColor Green
Get-ChildItem $dist -Filter *.zip | ForEach-Object { '{0,-52} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
if (Test-Path $rootExe) { '{0,-52} {1,8:N1} MB' -f 'OwOWinDeployer.exe (单文件，可直接运行)', ((Get-Item $rootExe).Length / 1MB) }

if ($Run) {
    # 只有单文件形态会在 build\ 根目录留下可独立运行的 exe；其它形态为多文件，已随中间目录清理。
    if (Test-Path $rootExe) { Start-Process $rootExe }
    else { Write-Host '「-Run」需要 singlefile 形态（其它形态为多文件，已随中间目录清理，请解压对应 zip 运行）。' -ForegroundColor Yellow }
}
