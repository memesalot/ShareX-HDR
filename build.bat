@echo off
setlocal

cd /d "%~dp0"

echo Building ShareX solution (Release x64)...
dotnet build ShareX.sln -c Release -p:Platform=x64
if %ERRORLEVEL% neq 0 (
    echo Build failed.
    exit /b %ERRORLEVEL%
)

echo.
echo Running ShareX.Setup...
dotnet run --project ShareX.Setup -c Release -p:Platform=x64 -- -Job Release -Platform x64 -Silent
if %ERRORLEVEL% neq 0 (
    echo Setup failed.
    exit /b %ERRORLEVEL%
)

echo.
echo Done. Output is in: %~dp0Output
