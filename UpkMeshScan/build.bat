@echo off
setlocal
cd /d "%~dp0"
echo Building UpkMeshScan...
dotnet publish UpkMeshScan.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
if errorlevel 1 goto failed
echo.
echo Build OK: %~dp0publish\UpkMeshScan.exe
goto end
:failed
echo.
echo BUILD FAILED
:end
pause
