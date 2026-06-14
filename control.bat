@echo off
rem DokiCode bootstrap — builds the app on first run, creates the premium DokiCode.lnk
rem launcher (arc-reactor icon, NO console window), then opens it.
rem   First run:  double-click this once.   After that:  double-click DokiCode.lnk.
setlocal
set "EXE=%~dp0control\bin\Release\net9.0-windows\DokiCode.Control.exe"
if not exist "%EXE%" (
  echo Building DokiCode ^(first run^)...
  dotnet build "%~dp0control\DokiCode.Control.csproj" -c Release || (echo Build failed. & pause & exit /b 1)
)
rem create/refresh the icon'd, console-free shortcut, then hand off to it
pwsh -NoProfile -File "%~dp0control\make-shortcut.ps1" >nul 2>&1
start "" "%EXE%"
