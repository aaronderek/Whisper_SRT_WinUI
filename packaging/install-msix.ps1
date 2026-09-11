<#
  install-msix.ps1  —  信任自签名证书并安装 MSIX

  权限模型（尽量减少提权面）：
    1) 先尝试**不提权**安装。证书已在「本地计算机\受信任人」时（重复安装/升级场景）无需管理员。
    2) 只有安装因证书不受信任而失败时，才请求提权，而且提权进程只做一件事：
       把测试证书导入「本地计算机\受信任人」（并清理旧版脚本误装进受信任根的同名证书）。
       它不再以管理员身份执行本脚本文件，因此不存在「用户可写目录里的脚本被提权执行」的问题。
    3) Add-AppxPackage 始终在当前（非提权）进程里执行。

  产物：packaging\build\WhisperSubtitler_<ver>_x64.msix（由 build-msix.bat 生成）
#>
$ErrorActionPreference = "Stop"

$PackagingDir = $PSScriptRoot
$Cer  = Join-Path $PackagingDir "WhisperSubtitler_TestCert.cer"
$Msix = Get-ChildItem (Join-Path $PackagingDir "build") -Filter *.msix -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not (Test-Path $Cer)) { throw "找不到证书 $Cer，请先运行 build-msix.bat。" }
if (-not $Msix)            { throw "找不到 .msix，请先运行 build-msix.bat。" }

$CertSubject = "CN=WhisperSubtitler Dev"

function Install-Package {
    Write-Host "==> 安装: $($Msix.Name)"
    # -ForceUpdateFromAnyVersion：允许同版本/降级覆盖，避免版本号没涨时卡住
    Add-AppxPackage -Path $Msix.FullName -ForceUpdateFromAnyVersion
}

Write-Host "==> 先尝试不提权安装 ..."
try {
    Install-Package
    Write-Host "==> 免提权安装成功（测试证书已在受信任人）。"
} catch {
    Write-Host "==> 免提权安装失败：$($_.Exception.Message)"
    Write-Host "==> 需要管理员把测试证书导入「本地计算机\受信任人」，正在请求提权 ..."

    # 提权进程只执行这一段命令，不执行任何脚本文件；路径按单引号转义防止注入
    $cerLiteral = $Cer.Replace("'", "''")
    $elevated = @"
Import-Certificate -FilePath '$cerLiteral' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
Get-ChildItem Cert:\LocalMachine\Root | Where-Object { `$_.Subject -eq '$CertSubject' } | Remove-Item -Force -ErrorAction SilentlyContinue
"@
    Start-Process powershell -Verb RunAs -Wait -ArgumentList @("-NoProfile", "-Command", $elevated)

    Install-Package
    Write-Host "==> 证书已信任，安装成功。"
}

Write-Host ""
Write-Host "完成！在开始菜单搜索 “Whisper 字幕生成器” 即可启动。"
Write-Host "（卸载：设置 -> 应用，或 PowerShell: Get-AppxPackage *WhisperSubtitler* | Remove-AppxPackage）"
Write-Host ""
# 双击运行时停一下让用户看到结果；非交互调用（自动化/CI）时直接结束
try { Read-Host "按回车关闭" } catch { }
