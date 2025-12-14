' TerminalHub Launcher
' サーバーを非表示で起動し、Chromeアプリモードで開く

Option Explicit

Dim WshShell, fso, appPath, chromePath, httpPort
Dim maxWaitSeconds, waitInterval, totalWait, serverReady

Set WshShell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

' ポート設定（HTTPのみ使用 - HTTPS証明書不要）
httpPort = 5080

' 待機設定
maxWaitSeconds = 30  ' 最大30秒待機
waitInterval = 1000  ' 1秒ごとにチェック

' アプリケーションのパスを取得（このスクリプトと同じフォルダ）
appPath = fso.GetParentFolderName(WScript.ScriptFullName)

' Chromeのパスを探す
chromePath = ""
If fso.FileExists("C:\Program Files\Google\Chrome\Application\chrome.exe") Then
    chromePath = "C:\Program Files\Google\Chrome\Application\chrome.exe"
ElseIf fso.FileExists("C:\Program Files (x86)\Google\Chrome\Application\chrome.exe") Then
    chromePath = "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
ElseIf fso.FileExists(WshShell.ExpandEnvironmentStrings("%LOCALAPPDATA%") & "\Google\Chrome\Application\chrome.exe") Then
    chromePath = WshShell.ExpandEnvironmentStrings("%LOCALAPPDATA%") & "\Google\Chrome\Application\chrome.exe"
End If

' サーバーを非表示で起動 (0 = 非表示)
' コマンドライン引数でURLを指定（HTTPのみ - 証明書不要）
WshShell.Run """" & appPath & "\TerminalHub.exe"" --urls ""http://localhost:" & httpPort & """", 0, False

' サーバーの起動を待つ（最大30秒）
totalWait = 0
serverReady = False

Do While totalWait < (maxWaitSeconds * 1000) And Not serverReady
    WScript.Sleep waitInterval
    totalWait = totalWait + waitInterval

    ' サーバーが起動したか確認
    serverReady = IsServerReady("http://localhost:" & httpPort)
Loop

' Chromeアプリモードで開く（HTTPで接続）
If chromePath <> "" Then
    WshShell.Run """" & chromePath & """ --app=http://localhost:" & httpPort, 1, False
Else
    ' Chromeが見つからない場合はデフォルトブラウザで開く
    WshShell.Run "http://localhost:" & httpPort, 1, False
End If

Set fso = Nothing
Set WshShell = Nothing

' サーバーが起動しているか確認する関数
Function IsServerReady(url)
    On Error Resume Next
    Dim http
    Set http = CreateObject("MSXML2.ServerXMLHTTP.6.0")
    http.setTimeouts 1000, 1000, 1000, 1000
    http.Open "GET", url, False
    http.Send

    If Err.Number = 0 And http.Status >= 200 And http.Status < 500 Then
        IsServerReady = True
    Else
        IsServerReady = False
    End If

    On Error GoTo 0
    Set http = Nothing
End Function
