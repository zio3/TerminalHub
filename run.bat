@echo off
chcp 65001 >nul

:: TerminalHub ビルド＆起動スクリプト
:: ソースからビルドしてApp Modeで起動します

cd /d "%~dp0"

set HTTP_PORT=5080

echo ========================================
echo   TerminalHub ビルド＆起動
echo ========================================
echo.

:: 1. Publish
echo [1/2] アプリケーションをビルド中...
if exist "publish" rd /s /q "publish"
dotnet publish TerminalHub/TerminalHub.csproj -c Release -r win-x64 --self-contained true -o ./publish
if %errorlevel% neq 0 (
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

:: Chromeを探してApp Modeで起動（3秒後）
set "CHROME_PATH="
if exist "C:\Program Files\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=C:\Program Files\Google\Chrome\Application\chrome.exe"
) else if exist "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
) else if exist "%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"
)

:: サーバーをバックグラウンドで起動
start "" publish\TerminalHub.exe --urls "http://localhost:%HTTP_PORT%"

:: 3秒待ってからブラウザを開く
timeout /t 3 /nobreak >nul
if defined CHROME_PATH (
    start "" "%CHROME_PATH%" --app=http://localhost:%HTTP_PORT%
) else (
    start http://localhost:%HTTP_PORT%
)

echo.
echo ========================================
echo   TerminalHub 起動完了
echo ========================================
echo.
echo   http://localhost:%HTTP_PORT%
echo.
pause
