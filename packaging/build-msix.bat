@echo off
REM 构建并自签名 MSIX（无需管理员）。双击即可。
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-msix.ps1"
echo.
pause
