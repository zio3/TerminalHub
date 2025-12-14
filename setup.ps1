# TerminalHub セットアップスクリプト
# 使用方法: .\setup.ps1
# 管理者権限で実行してください

param(
    [switch]$SkipBuild  # ビルドをスキップする場合
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "========================================"
Write-Host "  TerminalHub セットアップスクリプト"
Write-Host "========================================"
Write-Host ""

# 管理者権限チェック
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[警告] 管理者権限がありません。" -ForegroundColor Yellow
    Write-Host "       .NET SDKのインストールには管理者権限が必要です。" -ForegroundColor Yellow
    Write-Host "       右クリック→「管理者として実行」で再実行してください。" -ForegroundColor Yellow
    Write-Host ""
    Read-Host "Enterキーで終了"
    exit 1
}

# .NET SDK 9.0 のチェック
Write-Host "[1/4] .NET SDK のバージョンを確認中..." -ForegroundColor Cyan

$dotnetFound = $false
$dotnet9Found = $false

try {
    $dotnetVersion = dotnet --version 2>$null
    if ($dotnetVersion) {
        $dotnetFound = $true
        $sdkList = dotnet --list-sdks 2>$null
        if ($sdkList -match "^9\.") {
            $dotnet9Found = $true
        }
    }
} catch {
    $dotnetFound = $false
}

if ($dotnet9Found) {
    Write-Host "[OK] .NET SDK 9.0 が検出されました。" -ForegroundColor Green
    dotnet --version
} else {
    Write-Host "[INFO] .NET SDK 9.0 が見つかりません。インストールを開始します..." -ForegroundColor Yellow
    Write-Host ""

    # winget でインストールを試行
    $wingetPath = Get-Command winget -ErrorAction SilentlyContinue
    if ($wingetPath) {
        Write-Host "winget を使用して .NET SDK 9.0 をインストール中..."
        try {
            winget install Microsoft.DotNet.SDK.9 --accept-source-agreements --accept-package-agreements
            if ($LASTEXITCODE -eq 0) {
                Write-Host "[OK] .NET SDK 9.0 のインストールが完了しました。" -ForegroundColor Green
                Write-Host "[注意] 新しいPowerShellウィンドウを開いて再度実行してください。" -ForegroundColor Yellow
                Read-Host "Enterキーで終了"
                exit 0
            } else {
                throw "winget でのインストールに失敗しました。"
            }
        } catch {
            Write-Host "[エラー] $_" -ForegroundColor Red
            Write-Host "手動でインストールしてください: https://dotnet.microsoft.com/download/dotnet/9.0" -ForegroundColor Yellow
            Read-Host "Enterキーで終了"
            exit 1
        }
    } else {
        Write-Host "[エラー] winget が見つかりません。" -ForegroundColor Red
        Write-Host ""
        Write-Host "以下のいずれかの方法で .NET SDK 9.0 をインストールしてください:" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "  1. 公式サイトからダウンロード:" -ForegroundColor White
        Write-Host "     https://dotnet.microsoft.com/download/dotnet/9.0" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "  2. Microsoft Store から winget をインストール後、再実行" -ForegroundColor White
        Write-Host ""

        # ダウンロードリンクを開くか確認
        $openLink = Read-Host "ブラウザでダウンロードページを開きますか？ (Y/N)"
        if ($openLink -eq "Y" -or $openLink -eq "y") {
            Start-Process "https://dotnet.microsoft.com/download/dotnet/9.0"
        }
        exit 1
    }
}

Write-Host ""

# Node.js のチェック（オプション - npm scripts用）
Write-Host "[2/4] Node.js のバージョンを確認中..." -ForegroundColor Cyan

$nodeFound = Get-Command node -ErrorAction SilentlyContinue
if ($nodeFound) {
    Write-Host "[OK] Node.js が検出されました。" -ForegroundColor Green
    node --version
} else {
    Write-Host "[INFO] Node.js が見つかりません（オプション）。" -ForegroundColor Yellow
    Write-Host "       npm scripts を使用する場合はインストールしてください。"
    Write-Host "       https://nodejs.org/"
}

Write-Host ""

# プロジェクトのビルド
if (-not $SkipBuild) {
    Write-Host "[3/4] プロジェクトをビルド中..." -ForegroundColor Cyan

    Push-Location $PSScriptRoot
    try {
        dotnet build TerminalHub/TerminalHub.csproj
        if ($LASTEXITCODE -ne 0) {
            throw "ビルドに失敗しました。"
        }
        Write-Host "[OK] ビルドが完了しました。" -ForegroundColor Green
    } catch {
        Write-Host "[エラー] $_" -ForegroundColor Red
        Pop-Location
        Read-Host "Enterキーで終了"
        exit 1
    }
    Pop-Location
} else {
    Write-Host "[3/4] ビルドをスキップしました。" -ForegroundColor Yellow
}

Write-Host ""

# 完了メッセージ
Write-Host "[4/4] セットアップ完了！" -ForegroundColor Green
Write-Host ""
Write-Host "========================================"
Write-Host "  起動方法:"
Write-Host "========================================"
Write-Host ""
Write-Host "  PowerShell:" -ForegroundColor Cyan
Write-Host "    .\start.ps1              - バックグラウンドで起動"
Write-Host "    .\start.ps1 -Foreground  - フォアグラウンドで起動"
Write-Host "    .\stop.ps1               - 停止"
Write-Host ""
Write-Host "  npm（Node.js必要）:" -ForegroundColor Cyan
Write-Host "    npm run start            - フォアグラウンドで起動"
Write-Host "    npm run start:background - バックグラウンドで起動"
Write-Host "    npm run stop             - 停止"
Write-Host ""
Write-Host "========================================"

Read-Host "Enterキーで終了"
