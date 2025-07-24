param(
    [switch]$Foreground = $false,
    [switch]$NoBrowser = $false
)

# デフォルトポート設定（環境変数で上書き可能）
$httpPort = if ($env:TERMINALHUB_HTTP_PORT) { [int]$env:TERMINALHUB_HTTP_PORT } else { 5000 }
$httpsPort = if ($env:TERMINALHUB_HTTPS_PORT) { [int]$env:TERMINALHUB_HTTPS_PORT } else { 5001 }

# 利用可能なポートを検索する関数
function Find-AvailablePort {
    param([int]$StartPort)
    
    $port = $StartPort
    while ($port -lt 65535) {
        $tcpClient = New-Object System.Net.Sockets.TcpClient
        try {
            $tcpClient.Connect("localhost", $port)
            $tcpClient.Close()
            $port++
        }
        catch {
            return $port
        }
    }
    throw "利用可能なポートが見つかりません"
}

# ポートが使用中の場合は別のポートを探す
try {
    $tcpClient = New-Object System.Net.Sockets.TcpClient
    $tcpClient.Connect("localhost", $httpPort)
    $tcpClient.Close()
    Write-Host "ポート $httpPort は既に使用中です。利用可能なポートを検索中..." -ForegroundColor Yellow
    $httpPort = Find-AvailablePort ($httpPort + 1)
    $httpsPort = Find-AvailablePort ($httpsPort + 1)
}
catch {
    # ポートが利用可能
}

Write-Host "起動設定:" -ForegroundColor Green
Write-Host "  HTTP:  http://localhost:$httpPort" -ForegroundColor Cyan
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

# ログファイル用のGUIDとパスを生成
$logGuid = [Guid]::NewGuid().ToString()
$logDir = [System.IO.Path]::GetTempPath()
$logFile = Join-Path $logDir "$logGuid.log"

# 空のログファイルを作成
New-Item -Path $logFile -ItemType File -Force | Out-Null

Write-Host "`nログファイル設定:" -ForegroundColor Green
Write-Host "  ログファイル: $logFile" -ForegroundColor Cyan

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
    
    dotnet run --project TerminalHub/TerminalHub.csproj --urls "https://localhost:$httpsPort;http://localhost:$httpPort" 2>&1 | Tee-Object -FilePath $logFile
} else {
    # バックグラウンド実行（デフォルト）
    # 一時的なスクリプトファイルを作成
    $tempScript = [System.IO.Path]::GetTempFileName() + ".ps1"
    $scriptContent = @"
Set-Location '$PWD'
dotnet run --project TerminalHub/TerminalHub.csproj --urls "https://localhost:$httpsPort;http://localhost:$httpPort" 2>&1 | Tee-Object -FilePath '$logFile' -Append
"@
    
    $scriptContent | Set-Content -Path $tempScript -Encoding UTF8
    
    $process = Start-Process -FilePath "powershell" `
        -ArgumentList "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $tempScript `
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
            -replace '{{HTTPS_PORT}}', $httpsPort `
            -replace '{{TEMP_SCRIPT}}', $tempScript
        
        # stop.ps1を生成
        $stopScript | Set-Content -Path "./stop.ps1" -Encoding UTF8
        
        # LogView.ps1を生成
        @"
# Auto-generated log viewer script
# Generated at: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
# Process ID: $($process.Id)
# Log file: $logFile

Write-Host "ログファイルを監視しています: $logFile" -ForegroundColor Green
Write-Host "停止するには Ctrl+C を押してください" -ForegroundColor Yellow
Write-Host ""

Get-Content "$logFile" -Wait
"@ | Set-Content -Path "./LogView.ps1" -Encoding UTF8
    } else {
        Write-Host "警告: stop.ps1.templateが見つかりません。" -ForegroundColor Yellow
        Write-Host "デフォルトのstop.ps1を生成します。" -ForegroundColor Yellow
        
        # フォールバック: 簡易版stop.ps1を生成
        @"
# Auto-generated stop script
Stop-Process -Id $($process.Id) -Force
if (Test-Path "$tempScript") {
    Remove-Item -Path "$tempScript" -Force
}
if (Test-Path "./LogView.ps1") {
    Remove-Item -Path "./LogView.ps1" -Force
}
Remove-Item -Path `$MyInvocation.MyCommand.Path -Force
"@ | Set-Content -Path "./stop.ps1" -Encoding UTF8
        
        # LogView.ps1も生成
        @"
# Auto-generated log viewer script
# Generated at: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
# Process ID: $($process.Id)
# Log file: $logFile

Write-Host "ログファイルを監視しています: $logFile" -ForegroundColor Green
Write-Host "停止するには Ctrl+C を押してください" -ForegroundColor Yellow
Write-Host ""

Get-Content "$logFile" -Wait
"@ | Set-Content -Path "./LogView.ps1" -Encoding UTF8
    }
    
    Write-Host "`nプロセスを起動しました:" -ForegroundColor Green
    Write-Host "  Process ID: $($process.Id)" -ForegroundColor Yellow
    Write-Host "  URL: https://localhost:$httpsPort" -ForegroundColor Cyan
    Write-Host "`n停止するには以下を実行:" -ForegroundColor Yellow
    Write-Host "  .\stop.ps1" -ForegroundColor White
    Write-Host "`nログを確認するには以下を実行:" -ForegroundColor Yellow
    Write-Host "  .\LogView.ps1" -ForegroundColor White
    
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