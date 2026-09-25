@echo off
rem Build NetPI from cmd.exe: runs build.ps1 with the same options.
rem   build                        build everything (Release); while NetPI runs, plugins hot-reload and a
rem                                new host is ready for its next start
rem   build -NextStart             while NetPI runs: nothing changes in it, its next start runs the new build
rem   build -Run                   build and start the desktop app
rem   build -Test                  build and run the unit test suites
rem   build -SkipWeb               don't run npm even if it is installed
rem   build -Configuration Debug   Debug instead of Release
rem   build /?                     build.ps1's help
rem Uses PowerShell 7 (pwsh) when it is installed, else Windows PowerShell.
setlocal
set "PS=powershell.exe"
where pwsh >nul 2>&1 && set "PS=pwsh"
if "%~1"=="/?" (
  "%PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -?
  exit /b 0
)
"%PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
