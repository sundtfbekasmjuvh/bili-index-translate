@echo off
rem Builds a framework-dependent win-x64 release into .\publish (requires .NET 8 SDK).
setlocal
cd /d "%~dp0.."
dotnet --version >nul 2>&1 || (echo .NET 8 SDK not found. Install from https://dotnet.microsoft.com/download/dotnet/8.0 & exit /b 1)
dotnet test tests\BiliIndexTranslate.Core.Tests\BiliIndexTranslate.Core.Tests.csproj -c Release --nologo || exit /b 1
dotnet publish src\BiliIndexTranslate.App\BiliIndexTranslate.App.csproj -c Release -r win-x64 --self-contained false -o publish --nologo || exit /b 1
echo.
echo Output: %cd%\publish\BiliIndexTranslate.exe  (needs the .NET 8 Desktop Runtime on the target machine)
endlocal
