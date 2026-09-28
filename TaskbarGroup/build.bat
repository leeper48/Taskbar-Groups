@echo off
setlocal
cd /d "%~dp0"
echo Building Windows Taskbar Group...
rem A running hover helper locks the exe: ask it to quit first (it then exits within a moment).
if exist publish\TaskbarGroup.exe start /wait "" publish\TaskbarGroup.exe --quit-helper
timeout /t 1 /nobreak >nul
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
