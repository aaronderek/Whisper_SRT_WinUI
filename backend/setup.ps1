<#
  setup.ps1 — 一键搭建 Whisper 转录后端 (faster-whisper, GPU / Blackwell)
  要求：已安装 Python 3.10 - 3.12 (64 位) 并加入 PATH。
  由 setup.bat 启动（cmd 对 UTF-8 批处理支持糟糕，中文逻辑全部放在这里）。
#>
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Fail([string]$msg) {
    Write-Host ""
    Write-Host "  $msg" -ForegroundColor Red
    Read-Host "按回车退出"
    exit 1
}

Write-Host ""
Write-Host "[1/4] 检查 Python..."
# 注意：未安装 Python 时，Windows 自带一个同名的商店占位 python.exe（0 字节，
# 运行只会弹出 Microsoft Store）。必须实际运行一次确认可用。
$pythonOk = $false
try {
    $ver = & python --version 2>&1
    if ($LASTEXITCODE -eq 0 -and "$ver" -match "^Python 3") { $pythonOk = $true }
} catch {}
if (-not $pythonOk) {
    Write-Host "  找不到可用的 python（或 PATH 上只有 Microsoft Store 的占位程序）。"
    Write-Host "  请安装 64 位 Python 3.10-3.12 并勾选 `"Add to PATH`"："
    Write-Host "  下载: https://www.python.org/downloads/windows/"
    Write-Host "  若已安装仍看到此消息，请在 设置->应用->高级应用设置->应用执行别名"
    Write-Host "  中关闭 python.exe / python3.exe 的商店别名。"
    Fail "Python 不可用。"
}
Write-Host "  检测到 $ver"

Write-Host ""
Write-Host "[2/4] 创建虚拟环境 .venv ..."
if (-not (Test-Path ".venv\Scripts\python.exe")) {
    & python -m venv .venv
    if ($LASTEXITCODE -ne 0) { Fail "创建虚拟环境失败。" }
} else {
    Write-Host "  .venv 已存在，跳过。"
}

Write-Host ""
Write-Host "[3/4] 安装依赖（首次较慢，需要下载数百 MB 的 CUDA 运行库）..."
& ".venv\Scripts\python.exe" -m pip install --upgrade pip
& ".venv\Scripts\python.exe" -m pip install -r requirements.txt
if ($LASTEXITCODE -ne 0) { Fail "依赖安装失败，请检查网络后重试。" }

Write-Host ""
Write-Host "[4/4] 检测 GPU / 环境 ..."
& ".venv\Scripts\python.exe" transcribe.py --check

Write-Host ""
Write-Host "============================================================"
Write-Host " 完成。若上面显示 `"cuda_device_count`": 1 且 ok=true，即代表"
Write-Host " GPU 已就绪。现在可以回到 WinUI 应用点击“重新检测”。"
Write-Host "============================================================"
Read-Host "按回车关闭"
