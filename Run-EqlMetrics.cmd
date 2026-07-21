@echo off
REM ============================================================
REM  EQL Metrics launcher
REM  Double-click to build (incrementally) and start the overlay.
REM  Uses the .NET SDK you already run "dotnet" with — no exe to ship.
REM ============================================================
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET SDK not found. Install the .NET 10 SDK, then run this again.
  pause & exit /b 1
)

echo Building EQL Metrics ^(first run takes a moment; later runs are quick^)...
dotnet build -c Release -v quiet
if errorlevel 1 (
  echo.
  echo Build failed — see the messages above.
  pause & exit /b 1
)

set "EXE=%~dp0bin\Release\net10.0-windows\EqlMetrics.exe"
if not exist "%EXE%" (
  echo Could not find "%EXE%".
  pause & exit /b 1
)

start "" "%EXE%"
exit /b 0
