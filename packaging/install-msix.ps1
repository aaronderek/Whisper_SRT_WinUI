<#
  install-msix.ps1  —  信任自签名证书并安装 MSIX（需要管理员，脚本会自动提权）
#>
$ErrorActionPreference = "Stop"

# 自动提权
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "需要管理员权限，正在请求提权 ..."
    Start-Process powershell -Verb RunAs -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`""
    )
    exit
}

$PackagingDir = $PSScriptRoot
$Cer  = Join-Path $PackagingDir "WhisperSubtitler_TestCert.cer"
$Msix = Get-ChildItem (Join-Path $PackagingDir "build") -Filter *.msix -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not (Test-Path $Cer)) { throw "找不到证书 $Cer，请先运行 build-msix.bat。" }
if (-not $Msix)            { throw "找不到 .msix，请先运行 build-msix.bat。" }

Write-Host "==> 信任证书 -> 本地计算机\受信任人（自签名叶证书 sideload 只需 TrustedPeople）"
Import-Certificate -FilePath $Cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
# 清理旧版本脚本误装进受信任根的同名证书
Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Subject -eq "CN=WhisperSubtitler Dev" } |
    ForEach-Object { try { Remove-Item $_.PSPath -Force } catch {} }

Write-Host "==> 安装: $($Msix.Name)"
Add-AppxPackage -Path $Msix.FullName -ForceUpdateFromAnyVersion

Write-Host ""
Write-Host "完成！在开始菜单搜索 “Whisper 字幕生成器” 即可启动。"
Write-Host "（卸载：设置 -> 应用，或 PowerShell: Get-AppxPackage *WhisperSubtitler* | Remove-AppxPackage）"
Write-Host ""
Read-Host "按回车关闭"
