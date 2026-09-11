# MSIX 自签名打包与安装

把 WinUI 应用打包成 **自签名 MSIX** 并本机安装（sideload）。整套流程两步、几乎全自动。

## 前置要求

- 已能正常构建主工程（.NET 8 SDK + Visual Studio 的 **.NET 桌面开发 / Windows App SDK** 工作负载）。
- Windows 10/11 SDK（随上面工作负载安装）——提供 `makeappx.exe` 与 `signtool.exe`，脚本会自动定位。
- Windows 允许 sideload：Win11 默认允许；如被组织策略限制，去 **设置 → 隐私和安全性 → 开发者选项** 打开。

## 两步安装

1. **打包**（无需管理员）：双击 `build-msix.bat`。
   它会：Visual Studio `MSBuild` 发布打包模型的自包含文件 → 生成自签名证书 → `makeappx` 打包 → `signtool` 签名。
   产物：
   - `packaging\build\WhisperSubtitler_1.0.0.0_x64.msix`
   - `packaging\WhisperSubtitler_TestCert.cer`（公钥证书）

2. **安装**（需要管理员）：右键 `install-msix.bat` → **以管理员身份运行**（或双击后在 UAC 点“是”）。
   它会把开发证书导入 **本地计算机\受信任人 / 受信任的根证书颁发机构**，再 `Add-AppxPackage` 安装。
   完成后开始菜单搜索“Whisper 字幕生成器”。

> 首次安装必须信任证书（第 2 步），否则 Windows 会拒绝这个自签名包。之后重新打包同一证书无需再次导入。

## 说明与排错

**为什么是自包含打包？** `WindowsPackageType=MSIX` + `WindowsAppSDKSelfContained=true` 生成与包身份匹配的完整运行文件，再由 `makeappx` 封装；安装端无需单独安装 Windows App SDK 运行时。WinUI 的发布目标由 Visual Studio MSBuild 执行，因为它包含 XAML/MRT 所需的专用任务。

**提示证书不受信任 / 无法安装？** 确认第 2 步已成功导入证书；必要时把证书也导入 **受信任的根证书颁发机构**：
`Import-Certificate -FilePath .\WhisperSubtitler_TestCert.cer -CertStoreLocation Cert:\LocalMachine\Root`（管理员）。

**Publisher 不匹配？** `AppxManifest.xml` 的 `Publisher` 必须等于证书 Subject（默认都是 `CN=WhisperSubtitler Dev`）。改一个就要两个一起改。

**资源/PRI：** 脚本会把 MSBuild 生成的应用 PRI/XBF 与 Windows App SDK 资源一起放入 payload，并生成包身份需要的 `resources.pri`；这些文件是 WinUI XAML 启动所必需的。

**Python 后端：** 包内包含 `transcribe.py`、`requirements.txt` 和 `setup.bat`。首次启动时应用会把它们同步到该包的可写 LocalAppData（`%LOCALAPPDATA%\Packages\<包族>\LocalCache\Local\WhisperSubtitler\backend`），以便创建 `.venv` 和安装依赖。

**卸载：** 设置 → 应用，或 `Get-AppxPackage *WhisperSubtitler* | Remove-AppxPackage`。

**改版本号：** 双击时默认 `1.0.0.0`；如需指定：`powershell -File build-msix.ps1 -Version 1.0.1.0`。
