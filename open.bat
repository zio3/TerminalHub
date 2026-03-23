@echo off
chcp 65001 >nul

:: TerminalHub ブラウザ起動スクリプト
:: 既に起動中のTerminalHubをApp Modeで開きます

set HTTP_PORT=5080

set "CHROME_PATH="
if exist "C:\Program Files\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=C:\Program Files\Google\Chrome\Application\chrome.exe"
) else if exist "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
) else if exist "%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe" (
    set "CHROME_PATH=%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"
)

if defined CHROME_PATH (
    start "" "%CHROME_PATH%" --app=http://localhost:%HTTP_PORT%
) else (
    start http://localhost:%HTTP_PORT%
)
