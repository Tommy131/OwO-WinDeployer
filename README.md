# win-provision (WinDeploy)

一键在任意 Windows 设备上复刻开发环境、应用与个人配置。详细设计见 [`docs/DESIGN.md`](docs/DESIGN.md)。

## 当前进度：M1（引擎打通 · CLI 先行）

数据模型 + catalog 种子 + 安装引擎（winget / winget-bundle / portable / git / conda / vscode-ext / script）+ CLI。
GUI（软件安装中心，见 DESIGN §9）将在 M3 实现。

## 构建与运行（需 .NET SDK 10）

```powershell
dotnet build WinDeploy.sln

# 列出全部软件
dotnet run --project src/WinDeploy.Cli -- list

# 预览 dev 预设将安装什么（不执行）
dotnet run --project src/WinDeploy.Cli -- plan --profile dev

# 执行安装
dotnet run --project src/WinDeploy.Cli -- apply --profile dev --yes
```

## 裸机引导

```powershell
irm https://raw.githubusercontent.com/Tommy131/win-provision/main/bootstrap/bootstrap.ps1 | iex
```

## 结构

```
catalog/        软件主清单 catalog.json + profiles/
configs/        配置仓库（与是否安装解耦）：vscode / git / ssh / env / lmstudio …
src/            WinDeploy.Core（引擎）+ WinDeploy.Cli
bootstrap/      bootstrap.ps1
docs/DESIGN.md  设计文档
```

> 安全：SSH 私钥每台设备新生成、永不入库；配置内敏感信息默认排除。
