@echo off
rem Runs the Core unit tests (state machine, placement, translation client, settings).
setlocal
cd /d "%~dp0.."
dotnet test tests\BiliIndexTranslate.Core.Tests\BiliIndexTranslate.Core.Tests.csproj -c Release --nologo
endlocal
