param(
    [switch]$Foreground = $false,
    [switch]$NoBrowser = $false
)

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
Write-Host "  HTTPS: https://localhost:$httpsPort" -ForegroundColor Cyan

# 既存のstop.ps1があれば実行
if (Test-Path "./stop.ps1") {
    Write-Host "既存のサーバーを停止しています..." -ForegroundColor Yellow
    & ./stop.ps1
    Start-Sleep -Seconds 1
}

# ビルドを実行（無言）
$buildOutput = & dotnet build TerminalHub/TerminalHub.csproj --nologo --verbosity quiet 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nビルドエラー:" -ForegroundColor Red
    Write-Host $buildOutput -ForegroundColor Red
    exit 1
}

# 環境変数を設定
$env:ASPNETCORE_URLS = "https://localhost:$httpsPort;http://localhost:$httpPort"

# フォアグラウンド実行の場合
if ($Foreground) {
    # フォアグラウンド実行
    if (-not $NoBrowser) {
        # 別プロセスでブラウザ起動を待機
        $browserJob = Start-Job -ScriptBlock {
            param($httpsPort, $httpPort)
            Start-Sleep -Seconds 3
            
            $maxAttempts = 30
            $attempt = 0
            $serverReady = $false
            
            while ($attempt -lt $maxAttempts -and -not $serverReady) {
                try {
                    $response = Invoke-WebRequest -Uri "https://localhost:$httpsPort" -UseBasicParsing -TimeoutSec 1 -SkipCertificateCheck
                    $serverReady = $true
                } catch {
                    try {
                        $response = Invoke-WebRequest -Uri "http://localhost:$httpPort" -UseBasicParsing -TimeoutSec 1
                        $serverReady = $true
                    } catch {
                        Start-Sleep -Seconds 1
                        $attempt++
                    }
                }
            }
            
            if ($serverReady) {
                Start-Process "https://localhost:$httpsPort"
            }
        } -ArgumentList $httpsPort, $httpPort
    }
    
    dotnet run --project TerminalHub/TerminalHub.csproj --urls "https://localhost:$httpsPort;http://localhost:$httpPort"
} else {
    # バックグラウンド実行（デフォルト）
    # プロセスを開始（環境変数を含めて起動）
    $process = Start-Process -FilePath "dotnet" `
        -ArgumentList "run", "--project", "TerminalHub/TerminalHub.csproj", "--urls", "https://localhost:$httpsPort;http://localhost:$httpPort" `
        -PassThru -WindowStyle Hidden
    
    # テンプレートファイルを読み込んで置換
    if (Test-Path "./stop.ps1.template") {
        $template = Get-Content "./stop.ps1.template" -Raw
        
        # プレースホルダーを置換
        $stopScript = $template `
            -replace '{{GENERATED_AT}}', (Get-Date -Format "yyyy-MM-dd HH:mm:ss") `
            -replace '{{PROCESS_ID}}', $process.Id `
            -replace '{{FOLDER}}', $currentFolder `
            -replace '{{HTTP_PORT}}', $httpPort `
            -replace '{{HTTPS_PORT}}', $httpsPort
        
        # stop.ps1を生成
        $stopScript | Set-Content -Path "./stop.ps1" -Encoding UTF8
    } else {
        Write-Host "警告: stop.ps1.templateが見つかりません。" -ForegroundColor Yellow
        Write-Host "デフォルトのstop.ps1を生成します。" -ForegroundColor Yellow
        
        # フォールバック: 簡易版stop.ps1を生成
        @"
# Auto-generated stop script
Stop-Process -Id $($process.Id) -Force
Remove-Item -Path `$MyInvocation.MyCommand.Path -Force
"@ | Set-Content -Path "./stop.ps1" -Encoding UTF8
    }
    
    Write-Host "`nプロセスを起動しました:" -ForegroundColor Green
    Write-Host "  Process ID: $($process.Id)" -ForegroundColor Yellow
    Write-Host "  URL: https://localhost:$httpsPort" -ForegroundColor Cyan
    Write-Host "`n停止するには以下を実行:" -ForegroundColor Yellow
    Write-Host "  .\stop.ps1" -ForegroundColor White
    
    # サーバーの起動を待つ
    if (-not $NoBrowser) {
        Write-Host "`nサーバーの起動を待っています..." -ForegroundColor Yellow
        $maxAttempts = 30
        $attempt = 0
        $serverReady = $false
        
        while ($attempt -lt $maxAttempts -and -not $serverReady) {
            try {
                $response = Invoke-WebRequest -Uri "https://localhost:$httpsPort" -UseBasicParsing -TimeoutSec 1 -SkipCertificateCheck
                $serverReady = $true
            } catch {
                # HTTPSが失敗したらHTTPを試す
                try {
                    $response = Invoke-WebRequest -Uri "http://localhost:$httpPort" -UseBasicParsing -TimeoutSec 1
                    $serverReady = $true
                } catch {
                    Start-Sleep -Seconds 1
                    $attempt++
                }
            }
        }
        
        if ($serverReady) {
            Write-Host "サーバーが起動しました。ブラウザを開いています..." -ForegroundColor Green
            Start-Process "https://localhost:$httpsPort"
        } else {
            Write-Host "警告: サーバーの起動確認がタイムアウトしました。" -ForegroundColor Yellow
            Write-Host "手動でブラウザを開いてください: https://localhost:$httpsPort" -ForegroundColor Yellow
        }
    }
    
    return $process.Id
}