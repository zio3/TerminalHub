; TerminalHub Inno Setup Script
; インストーラーを作成するには:
; 1. Inno Setup をインストール: https://jrsoftware.org/isdl.php
; 2. このファイルを Inno Setup Compiler で開く
; 3. Compile (Ctrl+F9) を実行

#define MyAppName "TerminalHub"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "TerminalHub"
#define MyAppURL "https://github.com/your-repo/TerminalHub"
#define MyAppExeName "TerminalHub.exe"

[Setup]
; アプリケーション情報
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; インストール先
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}

; 出力設定
OutputDir=output
OutputBaseFilename=TerminalHub-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes

; 権限設定（管理者権限不要でユーザーフォルダにインストール可能）
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; その他
WizardStyle=modern
DisableProgramGroupPage=yes

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startmenuicon"; Description: "スタートメニューにショートカットを作成"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; publishフォルダの全ファイルをインストール
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; バッチファイル（メインランチャー）
Source: "TerminalHub.bat"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; スタートメニュー - バッチファイルを使用
Name: "{group}\{#MyAppName}"; Filename: "{app}\TerminalHub.bat"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"; Tasks: startmenuicon

; デスクトップ - バッチファイルを使用
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\TerminalHub.bat"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; インストール後に起動するオプション
Filename: "{app}\TerminalHub.bat"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// アンインストール時にプロセスが実行中かチェック
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;

  // TerminalHub.exe が実行中かチェック
  if Exec('tasklist', '/FI "IMAGENAME eq TerminalHub.exe" /NH', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    // プロセスが見つかった場合、終了を促す
    if MsgBox('TerminalHub が実行中の可能性があります。' + #13#10 +
              'アンインストールを続行する前に、TerminalHub を終了してください。' + #13#10 + #13#10 +
              '続行しますか？', mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
    end;
  end;
end;
