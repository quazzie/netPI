@echo off
rem netPI desktop launcher — delegates to tools\launch-desktop.ps1 (astra-1 P0.1).
rem The shell must never run from the repo bin\: the ps1 stages the complete
rem desktop payload into ~/.netpi/app-cache/desktop/<staging-id>/launch-<ts>/,
rem byte-verifies it, sets launcher identity (NETPI_HOME / NETPI_PROJECT_ROOT /
rem NETPI_PLUGINS), and launches the staged copy.
rem
rem   run-desktop.bat            build, stage, launch (default)
rem   run-desktop.bat -NoBuild    stage + launch an existing build

cd /d "%~dp0"
pwsh -NoProfile -File tools\launch-desktop.ps1 %*
exit /b %errorlevel%
