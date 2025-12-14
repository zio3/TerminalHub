@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

echo ========================================
echo   TerminalHub セットアップスクリプト
echo ========================================
echo.

:: 管理者権限チェック
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [警告] 管理者権限がありません。
    echo        .NET SDKのインストールには管理者権限が必要です。
    echo        右クリック→「管理者として実行」で再実行してください。
    echo.
    pause
    exit /b 1
)

:: .NET SDK 9.0 のチェック
echo [1/4] .NET SDK のバージョンを確認中...
set "DOTNET_FOUND=0"
set "DOTNET_9_FOUND=0"

where dotnet >nul 2>&1
if %errorLevel% equ 0 (
    set "DOTNET_FOUND=1"
    for /f "tokens=*" %%i in ('dotnet --list-sdks 2^>nul ^| findstr /r "^9\."') do (
        set "DOTNET_9_FOUND=1"
    )
)

if "!DOTNET_9_FOUND!"=="1" (
    echo [OK] .NET SDK 9.0 が検出されました。
    dotnet --version
) else (
    echo [INFO] .NET SDK 9.0 が見つかりません。インストールを開始します...
    echo.

    :: winget でインストールを試行
    where winget >nul 2>&1
    if %errorLevel% equ 0 (
        echo winget を使用して .NET SDK 9.0 をインストール中...
        winget install Microsoft.DotNet.SDK.9 --accept-source-agreements --accept-package-agreements
        if %errorLevel% neq 0 (
            echo [エラー] winget でのインストールに失敗しました。
            echo 手動でインストールしてください: https://dotnet.microsoft.com/download/dotnet/9.0
            pause
            exit /b 1
        )
        echo [OK] .NET SDK 9.0 のインストールが完了しました。
        echo [注意] 新しいコマンドプロンプトを開いて再度実行してください。
        pause
        exit /b 0
    ) else (
        echo [エラー] winget が見つかりません。
        echo.
        echo 以下のいずれかの方法で .NET SDK 9.0 をインストールしてください:
        echo.
        echo   1. 公式サイトからダウンロード:
        echo      https://dotnet.microsoft.com/download/dotnet/9.0
        echo.
        echo   2. Microsoft Store から winget をインストール後、再実行
        echo.
        pause
        exit /b 1
    )
)

echo.

:: Node.js のチェック（オプション - npm scripts用）
echo [2/4] Node.js のバージョンを確認中...
where node >nul 2>&1
if %errorLevel% equ 0 (
    echo [OK] Node.js が検出されました。
    node --version
) else (
    echo [INFO] Node.js が見つかりません（オプション）。
    echo        npm scripts を使用する場合はインストールしてください。
    echo        https://nodejs.org/
)

echo.

:: プロジェクトのビルド
echo [3/4] プロジェクトをビルド中...
cd /d "%~dp0"
dotnet build TerminalHub/TerminalHub.csproj
if %errorLevel% neq 0 (
    echo [エラー] ビルドに失敗しました。
    pause
    exit /b 1
)
echo [OK] ビルドが完了しました。

echo.

:: 完了メッセージ
echo [4/4] セットアップ完了！
echo.
echo ========================================
echo   起動方法:
echo ========================================
echo.
echo   PowerShell:
echo     .\start.ps1           - バックグラウンドで起動
echo     .\start.ps1 -Foreground  - フォアグラウンドで起動
echo     .\stop.ps1            - 停止
echo.
echo   npm（Node.js必要）:
echo     npm run start         - フォアグラウンドで起動
echo     npm run start:background - バックグラウンドで起動
echo     npm run stop          - 停止
echo.
echo ========================================
pause
