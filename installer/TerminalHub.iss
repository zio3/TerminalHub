; TerminalHub Inno Setup Script
; インストーラーを作成するには:
; 1. Inno Setup をインストール: https://jrsoftware.org/isdl.php
; 2. このファイルを Inno Setup Compiler で開く
; 3. Compile (Ctrl+F9) を実行

#define MyAppName "TerminalHub"
#define MyAppVersion "1.0.57"
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

; 実行中のアプリケーションを自動検出して強制終了
CloseApplications=force
CloseApplicationsFilter=TerminalHub.exe
RestartApplications=no

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startmenuicon"; Description: "スタートメニューにショートカットを作成"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; publishフォルダの全ファイルをインストール（app-settings.jsonは除外）
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "app-settings.json"

; バッチファイル（メインランチャー）
Source: "TerminalHub.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "TerminalHub-App.bat"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; スタートメニュー - バッチファイルを使用
Name: "{group}\{#MyAppName}"; Filename: "{app}\TerminalHub.bat"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon
Name: "{group}\{#MyAppName} (App Mode)"; Filename: "{app}\TerminalHub-App.bat"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"; Tasks: startmenuicon

; デスクトップ - バッチファイルを使用
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\TerminalHub.bat"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Finish ページでの起動は [Code] 内のラジオボタン選択に応じて CurStepChanged(ssDone) で実行する。
; [Run] の postinstall を使うと標準のチェックボックスUIが出てしまうため、ここには登録しない。

[Code]
var
  LaunchLabel: TNewStaticText;
  LaunchModeNormal: TNewRadioButton;
  LaunchModeApp: TNewRadioButton;
  LaunchModeNone: TNewRadioButton;

// Finish ページにラジオボタンを動的に追加する
procedure CurPageChanged(CurPageID: Integer);
var
  BaseLeft, ControlWidth, CurrentTop: Integer;
begin
  if CurPageID = wpFinished then
  begin
    // 二重生成防止（戻る→進むした場合）
    if LaunchLabel <> nil then Exit;

    BaseLeft := WizardForm.RunList.Left;
    ControlWidth := WizardForm.FinishedPage.ClientWidth - BaseLeft;
    CurrentTop := WizardForm.RunList.Top;

    LaunchLabel := TNewStaticText.Create(WizardForm);
    LaunchLabel.Parent := WizardForm.FinishedPage;
    LaunchLabel.Left := BaseLeft;
    LaunchLabel.Top := CurrentTop;
    LaunchLabel.Width := ControlWidth;
    LaunchLabel.Caption := '起動モード:';
    CurrentTop := CurrentTop + LaunchLabel.Height + ScaleY(6);

    LaunchModeApp := TNewRadioButton.Create(WizardForm);
    LaunchModeApp.Parent := WizardForm.FinishedPage;
    LaunchModeApp.Left := BaseLeft + ScaleX(8);
    LaunchModeApp.Top := CurrentTop;
    LaunchModeApp.Width := ControlWidth - ScaleX(8);
    LaunchModeApp.Caption := 'アプリモード（Chrome アプリウィンドウで起動）';
    LaunchModeApp.Checked := True;
    CurrentTop := CurrentTop + LaunchModeApp.Height + ScaleY(4);

    LaunchModeNormal := TNewRadioButton.Create(WizardForm);
    LaunchModeNormal.Parent := WizardForm.FinishedPage;
    LaunchModeNormal.Left := LaunchModeApp.Left;
    LaunchModeNormal.Top := CurrentTop;
    LaunchModeNormal.Width := LaunchModeApp.Width;
    LaunchModeNormal.Caption := '通常モード（デフォルトブラウザで起動）';
    CurrentTop := CurrentTop + LaunchModeNormal.Height + ScaleY(4);

    LaunchModeNone := TNewRadioButton.Create(WizardForm);
    LaunchModeNone.Parent := WizardForm.FinishedPage;
    LaunchModeNone.Left := LaunchModeApp.Left;
    LaunchModeNone.Top := CurrentTop;
    LaunchModeNone.Width := LaunchModeApp.Width;
    LaunchModeNone.Caption := '起動しない';
  end;
end;

// インストール完了時、選択したモードで起動する
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssDone then
  begin
    // ラジオボタン未生成（サイレントインストール等）の場合はスキップ
    if LaunchModeApp = nil then Exit;

    if LaunchModeApp.Checked then
    begin
      Exec(ExpandConstant('{app}\TerminalHub-App.bat'), '', ExpandConstant('{app}'),
           SW_SHOWNORMAL, ewNoWait, ResultCode);
    end
    else if LaunchModeNormal.Checked then
    begin
      Exec(ExpandConstant('{app}\TerminalHub.bat'), '', ExpandConstant('{app}'),
           SW_SHOWNORMAL, ewNoWait, ResultCode);
    end;
    // LaunchModeNone: 起動しない（何もしない）
  end;
end;

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
