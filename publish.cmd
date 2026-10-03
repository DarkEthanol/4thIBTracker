@echo off
rem Produces a checked, self-contained release in publish\.
rem Once a GitHub remote exists, its owner/repository is embedded for updates.
rem Local publishing requires the shared desktop OAuth client in the
rem GOOGLE_OAUTH_CLIENT_ID and GOOGLE_OAUTH_CLIENT_SECRET environment variables.

if "%GOOGLE_OAUTH_CLIENT_ID%"=="" (
  echo GOOGLE_OAUTH_CLIENT_ID is not set.
  echo Use the GitHub release workflow or set both Google OAuth environment variables.
  pause
  exit /b 1
)

if "%GOOGLE_OAUTH_CLIENT_SECRET%"=="" (
  echo GOOGLE_OAUTH_CLIENT_SECRET is not set.
  echo Use the GitHub release workflow or set both Google OAuth environment variables.
  pause
  exit /b 1
)

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
