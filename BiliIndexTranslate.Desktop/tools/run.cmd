@echo off
rem Builds (if needed) and starts the app from source. Use --welcome to force the welcome window.
setlocal
cd /d "%~dp0.."
dotnet --version >nul 2>&1 || (echo .NET 8 SDK not found. Install from https://dotnet.microsoft.com/download/dotnet/8.0 & exit /b 1)
dotnet build src\BiliIndexTranslate.App\BiliIndexTranslate.App.csproj -c Release --nologo -v q || exit /b 1
start "" "src\BiliIndexTranslate.App\bin\Release\net8.0-windows\BiliIndexTranslate.exe" %*
endlocal
