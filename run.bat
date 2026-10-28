@echo off
cd /d "%~dp0"
if exist dist\Hizashi.exe (
  start "" dist\Hizashi.exe
  exit /b
)
echo Building and starting Hizashi (the first run takes a little while)...
dotnet run -c Release
