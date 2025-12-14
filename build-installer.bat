@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

:: TerminalHub インストーラービルドスクリプト
:: ダブルクリックで実行できます

cd /d "%~dp0"

echo ========================================
echo   TerminalHub インストーラービルド
echo ========================================
echo.

:: 1. Publish
echo [1/3] アプリケーションをpublish中...
dotnet publish TerminalHub/TerminalHub.csproj -c Release -r win-x64 --self-contained true -o ./publish
if !errorlevel! neq 0 (
    echo [エラー] Publishに失敗しました。
    pause
    exit /b 1
)
echo [OK] Publish完了
echo.

:: 2. Inno Setup のパスを探す
echo [2/3] Inno Setup を検索中...

set "ISCC_PATH="

:: Program Files (x86) をチェック
if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" (
    set "ISCC_PATH=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)

:: Program Files をチェック
if not defined ISCC_PATH (
    if exist "C:\Program Files\Inno Setup 6\ISCC.exe" (
        set "ISCC_PATH=C:\Program Files\Inno Setup 6\ISCC.exe"
    )
)

:: LOCALAPPDATA をチェック
if not defined ISCC_PATH (
    call :check_localappdata
)

if not defined ISCC_PATH (
    echo [エラー] Inno Setup が見つかりません。
    echo.
    echo Inno Setup をインストールしてください:
    echo   https://jrsoftware.org/isdl.php
    echo.
    echo または、手動でインストーラーを作成:
    echo   1. Inno Setup Compiler を開く
    echo   2. installer\TerminalHub.iss を開く
    echo   3. Compile [Ctrl+F9] を実行
    echo.
    pause
    exit /b 1
)

echo [OK] Inno Setup を検出: !ISCC_PATH!
echo.

:: 3. 出力ディレクトリを作成
if not exist "installer\output" mkdir "installer\output"

:: 4. インストーラーをビルド
echo [3/3] インストーラーをビルド中...
"!ISCC_PATH!" "installer\TerminalHub.iss"
if !errorlevel! neq 0 (
    echo [エラー] インストーラーのビルドに失敗しました。
    pause
    exit /b 1
)

echo.
echo ========================================
echo   ビルド完了！
echo ========================================
echo.
echo 出力ファイル: installer\output\TerminalHub-Setup-1.0.0.exe
echo.
echo 配布するには installer\output フォルダの exe を渡してください。
echo.
pause
exit /b 0

:check_localappdata
set "LOCALAPPDATA_PATH=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if exist "!LOCALAPPDATA_PATH!" (
    set "ISCC_PATH=!LOCALAPPDATA_PATH!"
)
exit /b 0
