@echo off
rem Build NetPI from cmd.exe: runs build.ps1 with the same options.
rem   build                        build into artifacts\dev\app; the running app is not touched
rem   build -Publish               install into artifacts\app: plugins hot-reload, a new host is
rem                                ready for its next start (the running app's chats are told which)
rem   build -Publish -NextStart    the running app gets nothing; its next start runs the new build
rem   build -Pending               what a restart would bring
rem   build -Discard               drop the staged build
rem   build -Run                   publish and start the desktop app
rem   build -Test                  build and run the unit test suites
rem   build -SkipWeb               don't run npm even if it is installed
rem   build -Configuration Debug   Debug instead of Release
rem   build /?                     build.ps1's help
rem Needs PowerShell 7 (pwsh); Windows PowerShell 5.1 lacks what the scripts use.
setlocal
set "PS=pwsh"
where pwsh >nul 2>&1 || (
  echo build: PowerShell 7 ^(pwsh^) is required - install it with: winget install Microsoft.PowerShell
  exit /b 1
)
if "%~1"=="/?" (
  "%PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -?
  exit /b 0
)
"%PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
