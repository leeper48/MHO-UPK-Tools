@echo off
setlocal

rem Builds AnimExportCli from the command line, no Visual Studio required.
rem
rem Works two ways:
rem   - Double-click this file directly: builds the AnimExportCli.csproj
rem     sitting next to it.
rem   - Set as the default handler for .csproj files, or send a .csproj to
rem     it via Send To: Windows passes the file's path in as %1, and that
rem     gets built instead.
rem
rem Always builds Debug|x64, matching bin\x64\Debug\net8.0-windows, which is
rem where every test in this project has been run from so far. To build
rem Release instead, change "-c Debug" below to "-c Release".

if "%~1"=="" goto usedefault
set "PROJECT_FILE=%~1"
goto havefile

:usedefault
set "PROJECT_FILE=%~dp0AnimExportCli.csproj"

:havefile
for %%I in ("%PROJECT_FILE%") do set "PROJECT_DIR=%%~dpI"

if exist "%PROJECT_FILE%" goto buildit
echo Could not find a .csproj to build.
echo Looked for: %PROJECT_FILE%
echo.
pause
exit /b 1

:buildit
echo Building %PROJECT_FILE% ...
echo.

dotnet build "%PROJECT_FILE%" -p:Platform=x64 -c Debug
set "BUILD_RESULT=%errorlevel%"

echo.
if "%BUILD_RESULT%"=="0" goto success
echo Build FAILED, exit code %BUILD_RESULT%. See errors above.
goto finish

:success
echo Build succeeded.
echo Output: %PROJECT_DIR%bin\x64\Debug\net8.0-windows\AnimExportCli.exe

:finish
echo.
pause
