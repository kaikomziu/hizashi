@echo off
cd /d "%~dp0"
echo Building a self-contained single-file exe (no .NET install needed to run it)...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
if errorlevel 1 (
  echo Build failed.
  exit /b 1
)
del /q dist\*.xml 2>nul
echo.
echo Done: dist\Hizashi.exe  (double-click to run)
echo.
echo Smaller build (about 3 MB, needs the .NET 9 Desktop Runtime):
echo   dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\lite
