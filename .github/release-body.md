一键在任意 Windows 设备上复刻开发环境、应用与个人配置。三种发行形态任选，均自带 `catalog/ configs/ assets/` 数据：

| 下载 | 说明 | 免装 .NET | 体积 |
|---|---|:--:|:--:|
| `…-win-x64-singlefile.zip` | **单文件版（推荐）**：解压后一个 `WinDeploy.exe` + 数据目录 | ✅ | 中 |
| `…-win-x64-with-runtime.zip` | **集成运行环境版**：多文件、自带 .NET 运行库，杀软启发式误报更低 | ✅ | 大 |
| `…-win-x64-framework.zip` | **纯 App 版**：体积最小，**需先装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)** | ❌ | 小 |
| `WinDeploy.exe` | 仅 GUI 单文件，供 `bootstrap.ps1` 直链下载 | ✅ | 中 |

> ⚠️ 程序未做代码签名时，Windows 可能提示「未知发布者」或被 Defender 拦截 —— 这属于无签名安装类软件的正常现象，可点「更多信息 → 仍要运行」，或参见 README 的「被拦截怎么办」。彻底解决需 Authenticode 代码签名（仓库已内置签名流水线，配置证书后自动生效）。
>
> 许可证：CC BY-NC-SA 4.0（署名 / 非商业 / 相同方式共享；作者保留商业权利）。
