@echo off
rem Produces a checked, self-contained release in publish\.
rem Once a GitHub remote exists, its owner/repository is embedded for updates.

cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass ^
  -File "%~dp0scripts\Publish-Release.ps1" ^
  -OutputDirectory "%~dp0publish" ^
  -Force

if errorlevel 1 (
  echo.
  echo Publish failed.
  pause
  exit /b 1
)

echo.
echo Done. Distribute publish\4thIBTracker.exe for the first updater-enabled release.
pause
