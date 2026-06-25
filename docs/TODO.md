# TODO — 待开发功能清单

> 下一轮集中开发的候选功能，按「投入产出比」分三档。完成后请勾选并补一句实现说明。
> 现状：安装中心 / 配置同步 / 环境采集恢复 / 系统概览维护 / WSL / 调优 / FTP / Cloudflare / 终端 均已实现（M1–M5）。

## 🥇 第一档：补全已经开了头的能力（顺手、闭环）

- [x] **智能体会话备份（Option A：只采小而关键的部分）** ✅
  - 实现：给 `EnvCaptureSource` 加 `IncludeDirs`（命名子目录递归采集，遵守脱敏与敏感名排除），接到 Claude（`projects\*\memory`、`memory`、`commands`、`agents`）与 Codex（`memory`）源。
  - 大体积 transcripts（`projects\<p>\*.jsonl`）/缓存/`auth.json` 因只采 `memory` 子目录而天然排除；复用既有 capture/restore 管线，恢复已递归遍历。

- [x] **配置漂移 / Diff 视图** ✅
  - 实现：`EnvCapture.PreviewApply` 干跑，逐文件标记 New/Changed/Same（按大小+字节比对）。「环境配置恢复」前列出将新增/覆盖的文件与计数；configs/ 为空或已一致时直接短路提示。
  - 注：当前覆盖恢复（restore）方向。capture 方向（windows-terminal 反复覆盖）建议用「杂项」里的彻底取消跟踪解决。

- [x] **「把当前勾选保存为 Profile」** ✅
  - 实现：安装中心「存为方案」按钮 → 输入名称 → 写 `catalog/profiles/<name>.json`（已存在则确认覆盖）→ 加入「方案」下拉。`CatalogLoader.SaveProfile`/`ProfileExists` 落在 Core。

## 🥈 第二档：可靠性 / 可观测

- [x] **部署报告（Deployment Report）** ✅
  - 实现：`DeployReport.ToHtml`（逐项 状态/耗时/信息 + 成功/失败/跳过 汇总，沿用 inventory 样式）。apply 实际执行后写入 `%APPDATA% 应用目录/reports/deploy-<stamp>.html` 并弹窗询问是否打开。

- [x] **SHA256 回填工具** ✅
  - 实现：CLI `windeploy hash [--all] [--only ids] [--write]` —— 下载便携项安装包算 SHA256，`--write` 用「定位 url 字符串就地插入」方式回写 catalog.json（保留注释/格式）。默认只读打印。

- [x] **自动化测试基线** ✅
  - 实现：`src/WinDeploy.Core.Tests`（xUnit，net10.0），47 个测试覆盖 `Selection.Resolve`、`CatalogLoader`、`Secrets.Redact`/`IsTextConfig`、i18n 三语键对齐+占位符、`CatalogValidator`。`dotnet test` 全绿。
  - 注：`EnvCapture.Glob()` 为 private，未直接测；改测公开纯逻辑。

## 🥉 第三档：进阶 / 锦上添花

- [x] **还原点（Restore Point）** ✅ — `RestorePoint.CreateAsync`（`Checkpoint-Computer`）+ 设置页「批量安装前创建系统还原点」开关；安装前创建，失败（需管理员且启用系统还原）时询问是否仍继续。
- [x] **定时导出** ✅ — App 新增无界面入口 `--capture <repoRoot>`（跑导出管线，仅非敏感，置于单实例守卫之前，GUI 开着也能跑）+ `ScheduledExport`（schtasks）每日/每周/登录时任务；采集页加「定时采集」开关+频率。
- [x] **远程 apply** ✅ — `RemoteDeploy`（内置 ssh.exe/scp.exe，仅密钥认证、无密码）+ 自包含对话框：测试连通 → 推送仓库到目标机 → 运行部署命令（默认 `windeploy apply --silent`）并显示输出；入口在配置同步页的卡片。

## 杂项 / 已知待办

- [ ] **彻底取消跟踪 `configs/windows-terminal/settings.json`**（像 vscode 那样改为不入库），否则采集会反复覆盖该模板。
- [ ] **打包发布 v1.2.4**：把当前已提交但未发布的工作（`7f7eda2` 修复 + `e7017d6` 环境采集/恢复）连同本清单中完成的功能一起，按发布流程（改 csproj `<Version>` + README ×2 + CHANGELOG）发版。

---

### 建议开发顺序
先做 **第一档 #1（会话备份）+ #2（Diff 视图）**：复用现有 EnvCapture/ConfigSync 管线、闭合「环境复刻」主线，且 #2 直接解决 windows-terminal 反复被覆盖的痛点。然后把这些与已提交未发布的工作一起打包成 **v1.2.4**。
