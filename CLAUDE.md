# owo-win-deployer (OwO! Win Deployer)

Windows 环境复刻器：一键在新设备安装软件/工具链/环境并同步个人配置。**完整设计见 `docs/DESIGN.md`（权威来源）。**

## 关键决定
- 技术栈：C# / .NET 10；引擎 `WinDeploy.Core`（纯库），CLI `WinDeploy.Cli`；GUI 为 WPF 自包含单 exe（M3）。
- 数据/引擎分离：`catalog/catalog.json`（软件主清单）+ `catalog/profiles/` + `configs/`（配置仓库，与是否安装解耦）。
- 安装方式：winget / winget-bundle / portable / git / conda / vscode-ext / script。
- 逐项可选：每项 `default` 决定是否预选（开发+系统=强制，其余可选）。
- 跨设备：路径用 `${DevRoot}` / `${ToolsDir}` 变量，首次设定（不假设 D 盘）。
- 安全：SSH 私钥每台新生成、永不入库；配置内 secrets 默认排除。
- 多语言（zh/en/de）：`WinDeploy.Core/I18n/Localizer`（共享，内嵌 `Resources/<lang>/*.json`，回退 当前→en→key）。XAML 用 `{DynamicResource S.<key>}`（镜像 `ThemeManager` 即时切换），代码用 `Localizer.T/Format`。首启按系统语言，设置页可切。**审计日志（AuditLog 正文）有意保留中文，作为诊断记录。**

## 路线图
M1 引擎打通（CLI）→ M2 配置同步+导出 → M3 WPF GUI（软件安装中心）→ M4 多机同步+发布 → **M5 系统管理与专业工具（系统概览/维护、WSL、系统调优、高级工具；开发人员模式门控）**。均已实现。

## 约定
- 加软件 = 改 `catalog.json`（主清单）+ `catalog/i18n/{en,de}.json`（软件 `summary` 译文，zh 用 catalog.json 原文），不动引擎。
- 加界面文案 = 在 `src/WinDeploy.Core/I18n/Resources/{en,zh,de}/<area>.json` 三语同步加 key（保持键集一致），XAML 用 `{DynamicResource S.<key>}`，代码用 `Localizer.T/Format`。改后跑 `scripts/check-i18n.ps1` 校验三语键对齐。
- Core 中匹配外部工具输出的中文（如 winget stdout 的 `Contains("已是最新")`）**绝不本地化**（已加 `// MATCHED:` 注释）。
- 提交信息不要加 AI 署名。
