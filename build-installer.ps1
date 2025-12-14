# TerminalHub インストーラービルドスクリプト
# 使用方法: .\build-installer.ps1

param(
    [switch]$SkipPublish  # publishをスキップする場合
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "========================================"
Write-Host "  TerminalHub インストーラービルド"
Write-Host "========================================"
Write-Host ""

Push-Location $PSScriptRoot

try {
    # 1. Publish
    if (-not $SkipPublish) {
        Write-Host "[1/3] アプリケーションをpublish中..." -ForegroundColor Cyan
        dotnet publish TerminalHub/TerminalHub.csproj -c Release -r win-x64 --self-contained true -o ./publish
        if ($LASTEXITCODE -ne 0) {
            throw "Publishに失敗しました。"
        }
        Write-Host "[OK] Publish完了" -ForegroundColor Green
    } else {
        Write-Host "[1/3] Publishをスキップしました" -ForegroundColor Yellow
    }

    Write-Host ""

    # 2. Inno Setup のパスを探す
    Write-Host "[2/3] Inno Setup を検索中..." -ForegroundColor Cyan

    $innoSetupPaths = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
    )

    $isccPath = $null
    foreach ($path in $innoSetupPaths) {
        if (Test-Path $path) {
            $isccPath = $path
            break
        }
    }

    if (-not $isccPath) {
        Write-Host "[エラー] Inno Setup が見つかりません。" -ForegroundColor Red
        Write-Host ""
        Write-Host "Inno Setup をインストールしてください:" -ForegroundColor Yellow
        Write-Host "  https://jrsoftware.org/isdl.php" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "または、手動でインストーラーを作成:" -ForegroundColor Yellow
        Write-Host "  1. Inno Setup Compiler を開く" -ForegroundColor White
        Write-Host "  2. installer\TerminalHub.iss を開く" -ForegroundColor White
        Write-Host "  3. Compile (Ctrl+F9) を実行" -ForegroundColor White

        $openLink = Read-Host "`nダウンロードページを開きますか？ (Y/N)"
        if ($openLink -eq "Y" -or $openLink -eq "y") {
            Start-Process "https://jrsoftware.org/isdl.php"
        }
        exit 1
    }

    Write-Host "[OK] Inno Setup を検出: $isccPath" -ForegroundColor Green
    Write-Host ""

    # 3. インストーラーをビルド
    Write-Host "[3/3] インストーラーをビルド中..." -ForegroundColor Cyan

    # 出力ディレクトリを作成
    if (-not (Test-Path "./installer/output")) {
        New-Item -ItemType Directory -Path "./installer/output" | Out-Null
    }

    & $isccPath "./installer/TerminalHub.iss"
    if ($LASTEXITCODE -ne 0) {
        throw "インストーラーのビルドに失敗しました。"
    }

    Write-Host ""
    Write-Host "[OK] インストーラーのビルドが完了しました！" -ForegroundColor Green
    Write-Host ""
    Write-Host "出力ファイル:" -ForegroundColor Cyan
    Get-ChildItem "./installer/output/*.exe" | ForEach-Object {
        $size = [math]::Round($_.Length / 1MB, 1)
        Write-Host "  $($_.Name) (${size}MB)" -ForegroundColor White
    }
    Write-Host ""
    Write-Host "配布するには installer\output フォルダの exe を渡してください。" -ForegroundColor Yellow

} catch {
    Write-Host "[エラー] $_" -ForegroundColor Red
    exit 1
} finally {
    Pop-Location
}
