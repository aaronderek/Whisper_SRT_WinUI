@echo off
REM ASCII-only launcher. Chinese text lives in setup.ps1 because cmd.exe
REM mis-parses UTF-8 batch files (chcp 65001 mid-file corrupts line reads).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1"
