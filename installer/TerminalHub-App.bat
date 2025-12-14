@echo off
chcp 65001 >nul
setlocal

:: TerminalHub ランチャー（アプリモード）
:: Chromeのアプリモードで起動します

cd /d "%~dp0"

:: ポート設定
set HTTP_PORT=5080
set HTTPS_PORT=7198

echo ========================================
echo   TerminalHub (App Mode)
echo ========================================
echo.
echo URL: https://localhost:%HTTPS_PORT%
echo.
echo 停止するには Ctrl+C を押すか、このウィンドウを閉じてください。
echo.

:: Chromeのパスを探す
set "CHROME_PATH="
if exist "C:\Program Files\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=C:\Program Files\Google\Chrome\Application\chrome.exe"
) else if exist "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
) else if exist "%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"
)

:: ブラウザを数秒後に起動（バックグラウンドで）
if defined CHROME_PATH (
    start /b cmd /c "timeout /t 3 /nobreak >nul && start "" "%CHROME_PATH%" --app=https://localhost:%HTTPS_PORT%"
) else (
    start /b cmd /c "timeout /t 3 /nobreak >nul && start https://localhost:%HTTPS_PORT%"
)

:: サーバー起動
set ASPNETCORE_URLS=https://localhost:%HTTPS_PORT%;http://localhost:%HTTP_PORT%
TerminalHub.exe
