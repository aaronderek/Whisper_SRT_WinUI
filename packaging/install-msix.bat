@echo off
REM 信任自签名证书并安装 MSIX。会弹出 UAC 提权，请点“是”。
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-msix.ps1"
