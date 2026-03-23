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
echo [1/3] アプリケーションをpublish中...
if exist "publish" (
    echo     古いpublishフォルダをクリーン中...
    rd /s /q "publish"
)
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
    echo     Inno Setup が見つかりません。ファイルコピーでインストールします。
    echo.
    goto :direct_install
)

echo [OK] Inno Setup を検出: !ISCC_PATH!
echo.

:: 3. インストーラーをビルド＆実行
if not exist "installer\output" mkdir "installer\output"

echo [3/3] インストーラーをビルド中...
"!ISCC_PATH!" "installer\TerminalHub.iss"
if !errorlevel! neq 0 (
    echo [エラー] インストーラーのビルドに失敗しました。
    pause
    exit /b 1
)
echo [OK] インストーラービルド完了
echo.

:: .issファイルからバージョンを取得してインストーラーパスを決定
for /f "tokens=3 delims= " %%A in ('findstr /C:"#define MyAppVersion" installer\TerminalHub.iss') do (
    set "APP_VERSION=%%~A"
)
set "INSTALLER_PATH=installer\output\TerminalHub-Setup-!APP_VERSION!.exe"

if not exist "!INSTALLER_PATH!" (
    echo [エラー] インストーラーが見つかりません: !INSTALLER_PATH!
    pause
    exit /b 1
)

echo インストーラーを起動します...
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

:: ==========================================
:: Inno Setup なしの直接インストール
:: ==========================================
:direct_install
set "INSTALL_DIR=%LOCALAPPDATA%\Programs\TerminalHub"

echo [3/3] ファイルコピーでインストール中...
echo     インストール先: !INSTALL_DIR!
echo.

:: インストール先を作成
if not exist "!INSTALL_DIR!" mkdir "!INSTALL_DIR!"

:: 既存のapp-settings.jsonをバックアップ
if exist "!INSTALL_DIR!\app-settings.json" (
    echo     既存の設定ファイルを保持します。
    copy "!INSTALL_DIR!\app-settings.json" "!INSTALL_DIR!\app-settings.json.bak" >nul 2>&1
)

:: ファイルをコピー
xcopy /E /Y /Q "publish\*" "!INSTALL_DIR!\" >nul
if !errorlevel! neq 0 (
    echo [エラー] ファイルのコピーに失敗しました。
    pause
    exit /b 1
)

:: app-settings.jsonを復元（バックアップがあれば）
if exist "!INSTALL_DIR!\app-settings.json.bak" (
    copy /Y "!INSTALL_DIR!\app-settings.json.bak" "!INSTALL_DIR!\app-settings.json" >nul 2>&1
    del "!INSTALL_DIR!\app-settings.json.bak" >nul 2>&1
)

:: バッチファイルをコピー
if exist "installer\TerminalHub.bat" (
    copy /Y "installer\TerminalHub.bat" "!INSTALL_DIR!\" >nul 2>&1
)
if exist "installer\TerminalHub-App.bat" (
    copy /Y "installer\TerminalHub-App.bat" "!INSTALL_DIR!\" >nul 2>&1
)

:: auth.jsonが未存在なら初期ファイルをコピー
if not exist "!INSTALL_DIR!\auth.json" (
    if exist "TerminalHub\auth.json" (
        copy "TerminalHub\auth.json" "!INSTALL_DIR!\" >nul 2>&1
    )
)

echo [OK] インストール完了
echo.
echo ========================================
echo   完了！
echo ========================================
echo.
echo インストール先: !INSTALL_DIR!
echo.
echo 起動方法:
echo   !INSTALL_DIR!\TerminalHub.exe
echo.
pause
exit /b 0

:check_localappdata
set "LOCALAPPDATA_PATH=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if exist "!LOCALAPPDATA_PATH!" (
    set "ISCC_PATH=!LOCALAPPDATA_PATH!"
)
exit /b 0
