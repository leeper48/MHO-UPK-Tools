@echo off
setlocal
cd /d "%~dp0"
echo Building MHO Package Modifier...
dotnet publish UpkMeshScan.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
if errorlevel 1 goto failed
echo.
echo Build OK: %~dp0publish\MHO_UPK_Mod.exe
goto end
:failed
echo.
echo BUILD FAILED
:end
pause
