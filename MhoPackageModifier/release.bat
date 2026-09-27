@echo off
setlocal
cd /d "%~dp0"
echo Building a release of MHO Package Modifier...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1"
if errorlevel 1 goto failed
goto end
:failed
echo.
echo RELEASE FAILED
:end
pause
