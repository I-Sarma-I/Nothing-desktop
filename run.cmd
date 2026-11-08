@echo off
cd /d "%~dp0"
if not exist "artifacts\Nothing.Desktop\Nothing.Desktop.exe" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
  if errorlevel 1 exit /b 1
)
start "" "%~dp0artifacts\Nothing.Desktop\Nothing.Desktop.exe" --connect
