@echo off
rem Builds a self-contained win-x64 release (no runtime install needed) into .\publish-sc.
setlocal
cd /d "%~dp0.."
dotnet publish src\BiliIndexTranslate.App\BiliIndexTranslate.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-sc --nologo || exit /b 1
echo Output: %cd%\publish-sc\BiliIndexTranslate.exe
endlocal
