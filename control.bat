@echo off
rem DokiCode Control — double-click to open the status/control window.
where pwsh >nul 2>&1 && (
  start "" pwsh -NoProfile -STA -File "%~dp0control.ps1"
) || (
  start "" powershell -NoProfile -STA -File "%~dp0control.ps1"
)
