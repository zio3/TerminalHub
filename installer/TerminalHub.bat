@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

:: TerminalHub ランチャー
:: ダブルクリックで起動できます

cd /d "%~dp0"

:: ポート設定（HTTPのみ - 証明書不要）
set HTTP_PORT=5080

:: ポートが既に使用中かチェック
:: localhost 側（127.0.0.1 / [::1]）の LISTENING だけを見る。Tailscale などが別アドレス（100.x.x.x 等）で同じポートを使っていても
:: 「起動済み」と誤判定しないようにする。末尾の空白まで含めて一致させ、:50801 など別ポートへの誤一致も防ぐ
set PORT_IN_USE=0
for /f "tokens=*" %%a in ('netstat -an ^| findstr "LISTENING" ^| findstr /C:"127.0.0.1:%HTTP_PORT% " /C:"[::1]:%HTTP_PORT% " 2^>nul') do (
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
