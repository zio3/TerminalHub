using Microsoft.JSInterop;
using TerminalHub.Components.Shared.BottomPanels;
using TerminalHub.Models;
using TerminalHub.Services;

namespace TerminalHub.Components.Pages;

/// <summary>
/// Root.razor の DOSターミナル関連機能
/// </summary>
public partial class Root
{
    #region DOSターミナル関連フィールド

    /// <summary>単一のDOSターミナルXTermインスタンス</summary>
    private IJSObjectReference? singleDosTerminal;

    /// <summary>現在DOSターミナルに接続しているセッションID</summary>
    private Guid? currentDosTerminalSessionId;

    /// <summary>DOSターミナルパネルの参照（タブID → パネル）</summary>
    private Dictionary<string, DosTerminalPanel> dosTerminalPanelRefs = new();

    /// <summary>現在のDOSターミナルパネル参照</summary>
    private DosTerminalPanel? currentDosTerminalPanelRef;

    /// <summary>DOSターミナルイベントハンドラー（セッションごと）</summary>
    private readonly Dictionary<Guid, (EventHandler<DataReceivedEventArgs> DataReceived, EventHandler ProcessExited)> _dosTerminalHandlers = new();

    #endregion

    #region DOSターミナル初期化・クリーンアップ

    /// <summary>
    /// DOSターミナルを初期化
    /// </summary>
    private async Task InitializeSingleDosTerminal()
    {
        try
        {
            if (activeSessionId == null) return;

            var currentSession = sessions.FirstOrDefault(s => s.SessionId == activeSessionId);
            if (currentSession == null) return;


            // ConPTYセッションがなければ作成
            if (currentSession.DosTerminalConPtySession == null)
            {
                var conPtyService = new ConPtyService(LoggerFactory.CreateLogger<ConPtyService>());
                var conPtySession = await conPtyService.CreateSessionAsync("cmd.exe", "", currentSession.FolderPath, 120, 30);

                currentSession.DosTerminalConPtySession = conPtySession;
                conPtySession.Start();

                // ハンドラーを作成して登録（後で解除可能にする）
                var sessionId = currentSession.SessionId;
                var dataReceivedHandler = new EventHandler<DataReceivedEventArgs>((sender, args) =>
                    OnDosTerminalDataReceived(sessionId, args.Data));
                var processExitedHandler = new EventHandler((sender, args) =>
                    OnDosTerminalExited(sessionId));

                conPtySession.DataReceived += dataReceivedHandler;
                conPtySession.ProcessExited += processExitedHandler;

                // 辞書に保存（クリーンアップ時に使用）
                _dosTerminalHandlers[sessionId] = (dataReceivedHandler, processExitedHandler);

                // ConPTYの初期化が完了するまで少し待つ
                await Task.Delay(100);
            }

            // DOM要素の存在を確認
            var elementExists = await JSRuntime.InvokeAsync<bool>("eval", @"
                !!document.getElementById('dos-terminal-container')
            ");

            if (!elementExists)
            {
                StateHasChanged();
                await Task.Delay(200); // DOM更新を待つ
            }

            // 新しいXTermを作成
            singleDosTerminal = await JSRuntime.InvokeAsync<IJSObjectReference>(
                "terminalFunctions.createMultiSessionTerminal",
                "dos-terminal-container",
                $"dos-terminal-{activeSessionId}",
                dotNetRef
            );

            currentDosTerminalSessionId = activeSessionId;

            // リサイズは再接続時に自動的に送られるため、ここでは明示的に送らない

            // フォーカスとリサイズ
            await JSRuntime.InvokeVoidAsync("eval", $@"
                (function() {{
                    const termObj = window.multiSessionTerminals && window.multiSessionTerminals['dos-terminal-{activeSessionId}'];
                    if (termObj && termObj.terminal) {{
                        termObj.terminal.focus();
                        if (termObj.fitAddon) {{
                            termObj.fitAddon.fit();
                        }}
                    }}
                }})()
            ");
        }
        catch (Exception ex)
        {
            toast?.ShowError($"DOSターミナルの初期化エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// DOSターミナルをクリーンアップ
    /// </summary>
    private async Task CleanupSingleDosTerminal()
    {
        try
        {
            if (singleDosTerminal != null)
            {

                // JavaScriptで破棄
                await JSRuntime.InvokeVoidAsync("eval", $@"
                    (function() {{
                        const terminalId = 'dos-terminal-{currentDosTerminalSessionId}';
                        const termObj = window.multiSessionTerminals && window.multiSessionTerminals[terminalId];
                        if (termObj) {{
                            termObj.terminal.dispose();
                            delete window.multiSessionTerminals[terminalId];
                        }}
                    }})()
                ");

                await singleDosTerminal.DisposeAsync();
                singleDosTerminal = null;
                currentDosTerminalSessionId = null;
            }
        }
        catch (Exception)
        {
            // Ignore DOS terminal cleanup errors
        }
    }

    #endregion

    #region DOSターミナルイベントハンドラー

    /// <summary>
    /// DOSターミナルからのデータ受信時の処理
    /// </summary>
    private async void OnDosTerminalDataReceived(Guid sessionId, string data)
    {
        try
        {
            // アクティブなセッションのDOSターミナルデータのみ表示
            if (singleDosTerminal != null && sessionId == currentDosTerminalSessionId)
            {
                await InvokeAsync(async () =>
                {
                    await singleDosTerminal.InvokeVoidAsync("write", data);
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "DOSターミナルデータ受信処理でエラー: SessionId={SessionId}", sessionId);
        }
    }

    /// <summary>
    /// DOSターミナルプロセス終了時の処理
    /// </summary>
    private void OnDosTerminalExited(Guid sessionId)
    {
        InvokeAsync(async () =>
        {
            var session = sessions.FirstOrDefault(s => s.SessionId == sessionId);
            if (session != null)
            {
                // 現在表示中のDOSターミナルの場合はクリーンアップ
                if (currentDosTerminalSessionId == sessionId)
                {
                    await CleanupSingleDosTerminal();
                }

                // イベントハンドラーを解除してから破棄
                CleanupDosTerminalHandlers(sessionId, session.DosTerminalConPtySession);

                session.DosTerminalConPtySession?.Dispose();
                session.DosTerminalConPtySession = null;

                // 再起動できるように通知
                StateHasChanged();
            }
        });
    }

    /// <summary>
    /// DOSターミナルのイベントハンドラーを解除
    /// </summary>
    private void CleanupDosTerminalHandlers(Guid sessionId, ConPtySession? conPtySession)
    {
        if (_dosTerminalHandlers.TryGetValue(sessionId, out var handlers))
        {
            if (conPtySession != null)
            {
                conPtySession.DataReceived -= handlers.DataReceived;
                conPtySession.ProcessExited -= handlers.ProcessExited;
            }
            _dosTerminalHandlers.Remove(sessionId);
        }
    }

    #endregion

    #region DOSターミナルパネル参照管理

    /// <summary>
    /// DOSターミナルパネルの参照を保存
    /// </summary>
    private void StoreDosTerminalPanelRef()
    {
        if (currentDosTerminalPanelRef != null)
        {
            var activeTab = bottomPanelTabs.FirstOrDefault(t => t.Id == activeBottomPanelTabId);
            if (activeTab != null && activeTab.Type == BottomPanelTabType.DosTerminal)
            {
                dosTerminalPanelRefs[activeTab.Id] = currentDosTerminalPanelRef;
            }
        }
    }

    #endregion
}
