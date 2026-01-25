@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

:: TerminalHub ランチャー
:: ダブルクリックで起動できます

cd /d "%~dp0"

:: ポート設定（HTTPのみ - 証明書不要）
set HTTP_PORT=5080

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
    start http://localhost:%HTTP_PORT%
    timeout /t 3 /nobreak >nul
    exit /b 0
)

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
