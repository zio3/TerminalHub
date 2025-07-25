# Simple start script for TerminalHub
# 単純に起動するだけのスクリプト

# デフォルトポート
$httpPort = 5080
$httpsPort = 7198

Write-Host "起動設定:" -ForegroundColor Green
Write-Host "  HTTP:  http://localhost:$httpPort" -ForegroundColor Cyan
Write-Host "  HTTPS: https://localhost:$httpsPort" -ForegroundColor Cyan

# 既存のstop.ps1があれば実行
if (Test-Path "./stop.ps1") {
    Write-Host "`n既存のサーバーを停止しています..." -ForegroundColor Yellow
    & ./stop.ps1
    Start-Sleep -Seconds 1
}

Write-Host ""
Write-Host "停止するには Ctrl+C を押してください" -ForegroundColor Yellow
Write-Host ""

# 単純に実行
dotnet run --project TerminalHub/TerminalHub.csproj --urls "https://localhost:$httpsPort;http://localhost:$httpPort"