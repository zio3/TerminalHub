@echo off
chcp 65001 >nul
setlocal

:: TerminalHub ランチャー
:: ダブルクリックで起動できます

cd /d "%~dp0"

:: ポート設定（HTTPのみ - 証明書不要）
set HTTP_PORT=5080

echo ========================================
echo   TerminalHub
echo ========================================
echo.
echo URL: http://localhost:%HTTP_PORT%
echo.
echo 停止するには Ctrl+C を押すか、このウィンドウを閉じてください。
echo.

:: ブラウザを数秒後に起動（バックグラウンドで）
start /b cmd /c "timeout /t 5 /nobreak >nul && start http://localhost:%HTTP_PORT%"

:: サーバー起動
TerminalHub.exe --urls "http://localhost:%HTTP_PORT%"
