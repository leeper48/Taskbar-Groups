@echo off
setlocal
cd /d "%~dp0"
echo Building Windows Taskbar Group...
dotnet publish TaskbarGroup.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o publish
if errorlevel 1 goto failed
echo.
echo Build OK: %~dp0publish\TaskbarGroup.exe
goto end
:failed
echo.
echo BUILD FAILED
:end
pause
