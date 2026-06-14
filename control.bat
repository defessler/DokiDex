@echo off
rem DokiCode Control — double-click to open the control panel (WPF, .NET 9).
rem Builds on first run if the exe isn't present yet. Same app as `doki.ps1 panel`.
setlocal
set "EXE=%~dp0control\bin\Release\net9.0-windows\DokiCode.Control.exe"
if not exist "%EXE%" (
  echo Building DokiCode Control ^(first run^)...
  dotnet build "%~dp0control\DokiCode.Control.csproj" -c Release || (echo Build failed. & pause & exit /b 1)
)
start "" "%EXE%"
