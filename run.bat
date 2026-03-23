@echo off
chcp 65001 >nul

:: TerminalHub ビルド＆起動スクリプト
:: ソースからビルドしてApp Modeで起動します

cd /d "%~dp0"

echo ========================================
echo   TerminalHub ビルド＆起動
echo ========================================
echo.

echo [1/2] アプリケーションをビルド中...
dotnet publish TerminalHub/TerminalHub.csproj -c Release -r win-x64 --self-contained true -o ./publish
if %errorlevel% neq 0 (
    echo [エラー] ビルドに失敗しました。
    pause
    exit /b 1
)
echo [OK] ビルド完了
echo.

echo [2/2] 起動中...
echo.

:: Chromeを探す
set "CHROME_PATH="
if exist "C:\Program Files\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=C:\Program Files\Google\Chrome\Application\chrome.exe"
) else if exist "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
) else if exist "%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"
)

:: サーバーをバックグラウンドで起動
start "" publish\TerminalHub.exe --urls http://localhost:5080

:: 3秒待ってからブラウザを開く
timeout /t 3 /nobreak >nul
if defined CHROME_PATH (
    start "" "%CHROME_PATH%" --app=http://localhost:5080
) else (
    start http://localhost:5080
)

echo.
echo ========================================
echo   TerminalHub 起動完了
echo ========================================
echo.
echo   http://localhost:5080
echo.
pause
