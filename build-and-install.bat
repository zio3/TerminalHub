@echo off
setlocal enabledelayedexpansion

cd /d "%~dp0"

echo ========================================
echo   TerminalHub Build and Install
echo ========================================
echo.

echo [1/3] Publishing...
if exist "publish" (
    echo     Cleaning old publish folder...
    rd /s /q "publish"
)
dotnet publish TerminalHub/TerminalHub.csproj -c Release -r win-x64 --self-contained true -o ./publish
if !errorlevel! neq 0 (
    echo [ERROR] Publish failed.
    pause
    exit /b 1
)
echo [OK] Publish complete
echo.

echo [2/3] Searching for Inno Setup...

set "ISCC_PATH="

if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" (
    set "ISCC_PATH=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)

if not defined ISCC_PATH (
    if exist "C:\Program Files\Inno Setup 6\ISCC.exe" (
        set "ISCC_PATH=C:\Program Files\Inno Setup 6\ISCC.exe"
    )
)

if not defined ISCC_PATH (
    call :check_localappdata
)

if not defined ISCC_PATH (
    echo     Inno Setup not found. Installing via file copy.
    echo.
    goto :direct_install
)

echo [OK] Inno Setup found: !ISCC_PATH!
echo.

if not exist "installer\output" mkdir "installer\output"

echo [3/3] Building installer...
"!ISCC_PATH!" "installer\TerminalHub.iss"
if !errorlevel! neq 0 (
    echo [ERROR] Installer build failed.
    pause
    exit /b 1
)
echo [OK] Installer build complete
echo.

for /f "tokens=3 delims= " %%A in ('findstr /C:"#define MyAppVersion" installer\TerminalHub.iss') do (
    set "APP_VERSION=%%~A"
)
set "INSTALLER_PATH=installer\output\TerminalHub-Setup-!APP_VERSION!.exe"

if not exist "!INSTALLER_PATH!" (
    echo [ERROR] Installer not found: !INSTALLER_PATH!
    pause
    exit /b 1
)

echo Launching installer...
echo.
start "" "!INSTALLER_PATH!"

echo ========================================
echo   Done!
echo ========================================
echo.
echo Follow the installer wizard to complete installation.
echo.
pause
exit /b 0

:: ==========================================
:: Direct install (no Inno Setup)
:: ==========================================
:direct_install
set "INSTALL_DIR=%LOCALAPPDATA%\Programs\TerminalHub"

echo [3/3] Installing via file copy...
echo     Install dir: !INSTALL_DIR!
echo.

if not exist "!INSTALL_DIR!" mkdir "!INSTALL_DIR!"

if exist "!INSTALL_DIR!\app-settings.json" (
    echo     Preserving existing settings...
    copy "!INSTALL_DIR!\app-settings.json" "!INSTALL_DIR!\app-settings.json.bak" >nul 2>&1
)

xcopy /E /Y /Q "publish\*" "!INSTALL_DIR!\" >nul
if !errorlevel! neq 0 (
    echo [ERROR] File copy failed.
    pause
    exit /b 1
)

if exist "!INSTALL_DIR!\app-settings.json.bak" (
    copy /Y "!INSTALL_DIR!\app-settings.json.bak" "!INSTALL_DIR!\app-settings.json" >nul 2>&1
    del "!INSTALL_DIR!\app-settings.json.bak" >nul 2>&1
)

if exist "installer\TerminalHub.bat" (
    copy /Y "installer\TerminalHub.bat" "!INSTALL_DIR!\" >nul 2>&1
)
if exist "installer\TerminalHub-App.bat" (
    copy /Y "installer\TerminalHub-App.bat" "!INSTALL_DIR!\" >nul 2>&1
)

if not exist "!INSTALL_DIR!\auth.json" (
    if exist "TerminalHub\auth.json" (
        copy "TerminalHub\auth.json" "!INSTALL_DIR!\" >nul 2>&1
    )
)

echo [OK] Install complete
echo.
echo ========================================
echo   Done!
echo ========================================
echo.
echo Install dir: !INSTALL_DIR!
echo.
echo To start:
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
