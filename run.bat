@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

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

:: .NET SDK 9.0 のチェック
set "DOTNET_9_FOUND=0"

where dotnet >nul 2>&1
if %errorLevel% equ 0 (
    for /f "tokens=*" %%i in ('dotnet --list-sdks 2^>nul ^| findstr /r "^9\."') do (
        set "DOTNET_9_FOUND=1"
    )
)

if "!DOTNET_9_FOUND!"=="0" (
    echo [エラー] .NET SDK 9.0 が見つかりません。
    echo.
    echo セットアップを実行してください:
    echo   setup.bat を右クリック→管理者として実行
    echo.
    echo または手動でインストール:
    echo   https://dotnet.microsoft.com/download/dotnet/9.0
    echo.
    pause
    exit /b 1
)

:: ビルド
echo ビルド中...
dotnet build TerminalHub/TerminalHub.csproj --nologo -v q >nul 2>&1
if %errorLevel% neq 0 (
    echo [エラー] ビルドに失敗しました。
    echo.
    dotnet build TerminalHub/TerminalHub.csproj
    pause
    exit /b 1
)

echo.
echo URL: https://localhost:%HTTPS_PORT%
echo.
echo 停止するには Ctrl+C を押すか、このウィンドウを閉じてください。
echo.

:: ブラウザを数秒後に起動（バックグラウンドで）
start /b cmd /c "timeout /t 4 /nobreak >nul && start https://localhost:%HTTPS_PORT%"

:: サーバー起動
dotnet run --project TerminalHub/TerminalHub.csproj --urls "https://localhost:%HTTPS_PORT%;http://localhost:%HTTP_PORT%"
