# owo-win-deployer (OwO! Win Deployer)

Windows 环境复刻器：一键在新设备安装软件/工具链/环境并同步个人配置。**完整设计见 `docs/DESIGN.md`（权威来源）。**

## 关键决定
- 技术栈：C# / .NET 10；引擎 `WinDeploy.Core`（纯库），CLI `WinDeploy.Cli`；GUI 为 WPF 自包含单 exe（M3）。
- 数据/引擎分离：`catalog/catalog.json`（软件主清单）+ `catalog/profiles/` + `configs/`（配置仓库，与是否安装解耦）。
- 安装方式：winget / winget-bundle / portable / git / conda / vscode-ext / script。
- 逐项可选：每项 `default` 决定是否预选（开发+系统=强制，其余可选）。
- 跨设备：路径用 `${DevRoot}` / `${ToolsDir}` 变量，首次设定（不假设 D 盘）。
- 安全：SSH 私钥每台新生成、永不入库；配置内 secrets 默认排除。

## 路线图
M1 引擎打通（CLI）→ M2 配置同步+导出 → M3 WPF GUI（软件安装中心）→ M4 多机同步+发布 → **M5 系统管理与专业工具（系统概览/维护、WSL、系统调优、高级工具；开发人员模式门控）**。均已实现。

## 约定
- 加软件 = 只改 `catalog.json`，不动引擎。
- 提交信息不要加 AI 署名。
