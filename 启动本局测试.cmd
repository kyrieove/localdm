@echo off
chcp 65001 >nul
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-local-test.ps1" %*
if errorlevel 1 pause
