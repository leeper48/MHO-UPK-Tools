@echo off
setlocal

rem Registers .csproj as opening with build.bat instead of Visual Studio.
rem THIS IS MACHINE-WIDE: every .csproj file you double-click, in any
rem project, will run through build.bat afterward -- not just this one.
rem If you ever want to open a .csproj in Visual Studio by double-clicking
rem it again, you'll need to change this back (see the note at the end).
rem
rem Needs to run as Administrator. Right-click this file and choose
rem "Run as administrator" -- running it normally will just fail below.

net session >nul 2>&1
if "%errorlevel%"=="0" goto haveadmin
echo This needs to run as Administrator.
echo Right-click this file and choose "Run as administrator", then try again.
echo.
pause
exit /b 1

:haveadmin
set "BUILD_SCRIPT=%~dp0build.bat"

if exist "%BUILD_SCRIPT%" goto havebuild
echo Could not find build.bat next to this script.
echo Expected it at: %BUILD_SCRIPT%
echo.
pause
exit /b 1

:havebuild
assoc .csproj=AnimExportCli.CsprojBuildScript
ftype AnimExportCli.CsprojBuildScript="%BUILD_SCRIPT%" "%%1"

echo.
echo Done -- at least for anything that still honors assoc/ftype directly
echo (Command Prompt, some shells).
echo.
echo Windows 10/11 often does NOT let a script change what double-clicking
echo actually does in File Explorer this way -- it protects that specific
echo setting from being changed by anything other than its own UI, to stop
echo programs from silently hijacking file types. If double-clicking a
echo .csproj still opens Visual Studio after this, that's what's happening,
echo and the reliable fix is the manual route:
echo   1. Right-click any .csproj file
echo   2. Open with -^> Choose another app
echo   3. Browse to build.bat, select it
echo   4. Check "Always use this app to open .csproj files"
echo   5. OK
echo.
echo To put it back to Visual Studio later, the same right-click -^> Open
echo with dialog lets you choose Visual Studio and check the same box --
echo that's the safe way back, since it picks from apps Windows already
echo knows about rather than guessing a path.
echo.
pause
