@echo off
chcp 65001 >nul
setlocal

:: TerminalHub ランチャー
:: ダブルクリックで起動できます

cd /d "%~dp0"

:: ポート設定
set HTTP_PORT=5080
set HTTPS_PORT=7198

echo ========================================
echo   TerminalHub
echo ========================================
echo.
echo URL: https://localhost:%HTTPS_PORT%
echo.
echo 停止するには Ctrl+C を押すか、このウィンドウを閉じてください。
echo.

:: ブラウザを数秒後に起動（バックグラウンドで）
start /b cmd /c "timeout /t 3 /nobreak >nul && start https://localhost:%HTTPS_PORT%"

:: サーバー起動
set ASPNETCORE_URLS=https://localhost:%HTTPS_PORT%;http://localhost:%HTTP_PORT%
TerminalHub.exe
