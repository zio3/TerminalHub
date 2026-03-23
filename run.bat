@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

:: TerminalHub ビルド＆起動スクリプト
:: ソースからビルドしてApp Modeで起動します

cd /d "%~dp0"

:: ポート設定
set HTTP_PORT=5080

echo ========================================
echo   TerminalHub ビルド＆起動
echo ========================================
echo.

:: ポートが既に使用中かチェック
set PORT_IN_USE=0
for /f "tokens=2" %%a in ('netstat -ano ^| findstr "LISTENING" 2^>nul') do (
    echo %%a | findstr /E ":%HTTP_PORT%" >nul 2>&1
    if !errorlevel!==0 set PORT_IN_USE=1
)

if !PORT_IN_USE!==1 (
    echo   サーバーは既に起動しています
    echo.
    echo ブラウザを開いています...
    call :open_browser
    timeout /t 3 /nobreak >nul
    exit /b 0
)

:: 1. Publish
echo [1/2] アプリケーションをビルド中...
if exist "publish" (
    rd /s /q "publish"
)
dotnet publish TerminalHub/TerminalHub.csproj -c Release -r win-x64 --self-contained true -o ./publish
if !errorlevel! neq 0 (
    echo [エラー] ビルドに失敗しました。
    pause
    exit /b 1
)
echo [OK] ビルド完了
echo.

:: 2. 起動
echo [2/2] 起動中...
echo.
echo URL: http://localhost:%HTTP_PORT%
echo 停止するには Ctrl+C を押すか、このウィンドウを閉じてください。
echo.

:: ブラウザを数秒後にApp Modeで起動
call :open_browser_delayed

:: サーバー起動
publish\TerminalHub.exe --urls "http://localhost:%HTTP_PORT%"
exit /b 0

:: ==========================================
:: ブラウザをApp Modeで開く
:: ==========================================
:open_browser
call :find_chrome
if defined CHROME_PATH (
    start "" "%CHROME_PATH%" --app=http://localhost:%HTTP_PORT%
) else (
    start http://localhost:%HTTP_PORT%
)
exit /b 0

:open_browser_delayed
call :find_chrome
if defined CHROME_PATH (
    start /b cmd /c "timeout /t 3 /nobreak >nul && start "" "%CHROME_PATH%" --app=http://localhost:%HTTP_PORT%"
) else (
    start /b cmd /c "timeout /t 3 /nobreak >nul && start http://localhost:%HTTP_PORT%"
)
exit /b 0

:find_chrome
set "CHROME_PATH="
if exist "C:\Program Files\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=C:\Program Files\Google\Chrome\Application\chrome.exe"
) else if exist "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
) else if exist "%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"
)
exit /b 0
