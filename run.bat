@echo off
cd /d "%~dp0"

echo ========================================
echo   TerminalHub Build and Launch
echo ========================================
echo.

echo [1/2] Building...
dotnet publish TerminalHub/TerminalHub.csproj -c Release -r win-x64 --self-contained true -o ./publish
if %errorlevel% neq 0 (
    echo [ERROR] Build failed.
    pause
    exit /b 1
)
echo [OK] Build complete
echo.

echo [2/2] Starting...
echo.

set "CHROME_PATH="
if exist "C:\Program Files\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=C:\Program Files\Google\Chrome\Application\chrome.exe"
) else if exist "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
) else if exist "%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"
)

start "" publish\TerminalHub.exe --urls http://localhost:5080

timeout /t 3 /nobreak >nul
if defined CHROME_PATH (
    start "" "%CHROME_PATH%" --app=http://localhost:5080
) else (
    start http://localhost:5080
)

echo.
echo ========================================
echo   http://localhost:5080
echo ========================================
echo.
pause
