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
