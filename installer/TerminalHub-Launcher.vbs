' TerminalHub Launcher
' サーバーを非表示で起動し、Chromeアプリモードで開く

Option Explicit

Dim WshShell, fso, appPath, chromePath, httpPort, httpsPort

Set WshShell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

' ポート設定
httpPort = 5080
httpsPort = 7198

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

' 環境変数を設定してサーバーを非表示で起動
WshShell.Environment("Process")("ASPNETCORE_URLS") = "https://localhost:" & httpsPort & ";http://localhost:" & httpPort

' サーバーを非表示で起動 (0 = 非表示)
WshShell.Run """" & appPath & "\TerminalHub.exe""", 0, False

' サーバーの起動を待つ
WScript.Sleep 3000

' Chromeアプリモードで開く
If chromePath <> "" Then
    WshShell.Run """" & chromePath & """ --app=https://localhost:" & httpsPort, 1, False
Else
    ' Chromeが見つからない場合はデフォルトブラウザで開く
    WshShell.Run "https://localhost:" & httpsPort, 1, False
End If

Set fso = Nothing
Set WshShell = Nothing
