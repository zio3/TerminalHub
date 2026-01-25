@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

:: TerminalHub ランチャー（アプリモード）
:: Chromeのアプリモードで起動します

cd /d "%~dp0"

:: ポート設定
set HTTP_PORT=5080

:: Chromeのパスを探す
set "CHROME_PATH="
if exist "C:\Program Files\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=C:\Program Files\Google\Chrome\Application\chrome.exe"
) else if exist "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
) else if exist "%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"
)

:: ポートが既に使用中かチェック（LISTENINGのみ - 2段階フィルタ）
set PORT_IN_USE=0
for /f "tokens=*" %%a in ('netstat -an ^| findstr ":%HTTP_PORT%" ^| findstr "LISTENING" 2^>nul') do (
    set PORT_IN_USE=1
)

if !PORT_IN_USE!==1 (
    echo ========================================
    echo   TerminalHubサーバーは既に起動しています
    echo ========================================
    echo.
    echo ブラウザを開いています...
    if defined CHROME_PATH (
        start "" "%CHROME_PATH%" --app=http://localhost:%HTTP_PORT%
    ) else (
        start http://localhost:%HTTP_PORT%
    )
    timeout /t 3 /nobreak >nul
    exit /b 0
)

echo ========================================
echo   TerminalHub (App Mode)
echo ========================================
echo.
echo URL: http://localhost:%HTTP_PORT%
echo.
echo 停止するには Ctrl+C を押すか、このウィンドウを閉じてください。
echo.

:: ブラウザを数秒後に起動（バックグラウンドで）
if defined CHROME_PATH (
    start /b cmd /c "timeout /t 3 /nobreak >nul && start "" "%CHROME_PATH%" --app=http://localhost:%HTTP_PORT%"
) else (
    start /b cmd /c "timeout /t 3 /nobreak >nul && start http://localhost:%HTTP_PORT%"
)

:: サーバー起動
TerminalHub.exe --urls "http://localhost:%HTTP_PORT%"
