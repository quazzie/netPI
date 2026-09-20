@echo off
rem netPI desktop launcher — builds (if needed) and starts the WinForms + WebView2 app.
rem The app spawns its own host, or reuses an already-running one on :5173.
cd /d "%~dp0"

echo Building NetPI.Desktop (Debug)...
dotnet build src\NetPI.Desktop -c Debug --nologo -v q
if errorlevel 1 (
    echo Build failed — see output above.
    exit /b 1
)

start "netPI" dotnet src\NetPI.Desktop\bin\Debug\net10.0-windows\netPI.Desktop.dll
