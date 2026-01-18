@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

:: TerminalHub インストーラービルド＆インストールスクリプト
:: ダブルクリックで実行できます

cd /d "%~dp0"

echo ========================================
echo   TerminalHub ビルド＆インストール
echo ========================================
echo.

:: 1. Publish
echo [1/4] アプリケーションをpublish中...
dotnet publish TerminalHub/TerminalHub.csproj -c Release -r win-x64 --self-contained true -o ./publish
if !errorlevel! neq 0 (
    echo [エラー] Publishに失敗しました。
    pause
    exit /b 1
)
echo [OK] Publish完了
echo.

:: 2. Inno Setup のパスを探す
echo [2/4] Inno Setup を検索中...

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
    pause
    exit /b 1
)

echo [OK] Inno Setup を検出: !ISCC_PATH!
echo.

:: 3. 出力ディレクトリを作成
if not exist "installer\output" mkdir "installer\output"

:: 4. インストーラーをビルド
echo [3/4] インストーラーをビルド中...
"!ISCC_PATH!" "installer\TerminalHub.iss"
if !errorlevel! neq 0 (
    echo [エラー] インストーラーのビルドに失敗しました。
    pause
    exit /b 1
)
echo [OK] インストーラービルド完了
echo.

:: 5. インストーラーを実行
echo [4/4] インストーラーを実行中...
set "INSTALLER_PATH=installer\output\TerminalHub-Setup-1.0.0.exe"

if not exist "!INSTALLER_PATH!" (
    echo [エラー] インストーラーが見つかりません: !INSTALLER_PATH!
    pause
    exit /b 1
)

echo.
echo インストーラーを起動します...
echo （インストールウィザードが表示されます）
echo.

start "" "!INSTALLER_PATH!"

echo ========================================
echo   完了！
echo ========================================
echo.
echo インストールウィザードに従ってインストールしてください。
echo.
pause
exit /b 0

:check_localappdata
set "LOCALAPPDATA_PATH=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if exist "!LOCALAPPDATA_PATH!" (
    set "ISCC_PATH=!LOCALAPPDATA_PATH!"
)
exit /b 0
