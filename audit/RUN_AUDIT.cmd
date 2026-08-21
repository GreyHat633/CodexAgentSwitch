@echo off
setlocal
cd /d "%~dp0"

echo Checking PowerShell syntax...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0CHECK_SYNTAX.ps1" -Target "%~dp0CAS_Universal_Economic_Audit.ps1"
if errorlevel 1 (
  echo.
  echo PowerShell syntax check failed.
  echo.
  pause
  exit /b 1
)

echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0CAS_Universal_Economic_Audit.ps1"
echo.
pause
