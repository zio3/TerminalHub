# Simple start script for TerminalHub
# 単純に起動するだけのスクリプト

# ポートマッピング設定
$portMappings = @{
    "TerminalHub-work1" = @{ http = 9011; https = 9431 }
    "TerminalHub-work2" = @{ http = 9012; https = 9432 }
    "TerminalHub-work3" = @{ http = 9013; https = 9433 }
}

# 現在のフォルダ名を取得
$currentFolder = Split-Path -Leaf (Get-Location)

# デフォルトポート
$defaultHttpPort = 5080
$defaultHttpsPort = 7198

# ポート設定を取得
if ($portMappings.ContainsKey($currentFolder)) {
    $httpPort = $portMappings[$currentFolder].http
    $httpsPort = $portMappings[$currentFolder].https
} else {
    Write-Host "ポートマッピングが見つかりません。デフォルトポートを使用します。" -ForegroundColor Yellow
    $httpPort = $defaultHttpPort
    $httpsPort = $defaultHttpsPort
}

Write-Host "起動設定:" -ForegroundColor Green
Write-Host "  フォルダ: $currentFolder" -ForegroundColor Cyan
Write-Host "  URL: https://localhost:$httpsPort" -ForegroundColor Cyan

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