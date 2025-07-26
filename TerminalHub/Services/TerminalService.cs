using Microsoft.JSInterop;
using TerminalHub.Constants;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    public class TerminalService : ITerminalService
    {
        private readonly IJSRuntime _jsRuntime;
        private readonly ILogger<TerminalService> _logger;

        public TerminalService(IJSRuntime jsRuntime, ILogger<TerminalService> logger)
        {
            _jsRuntime = jsRuntime;
            _logger = logger;
        }

        public async Task<IJSObjectReference?> InitializeTerminalAsync(Guid sessionId, DotNetObjectReference<object> dotNetRef)
        {
            var terminalId = $"terminal-{sessionId}";
            _logger.LogDebug("[InitializeTerminal] 開始: terminalId={TerminalId}", terminalId);

            // JavaScript側の既存ターミナルも削除
            _logger.LogDebug("[InitializeTerminal] 既存ターミナルをクリーンアップ");
            try
            {
                await _jsRuntime.InvokeVoidAsync("terminalFunctions.cleanupTerminal", sessionId.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InitializeTerminal] クリーンアップエラー");
            }

            _logger.LogDebug("[InitializeTerminal] 新しいターミナルを作成");
            try
            {
                var terminal = await _jsRuntime.InvokeAsync<IJSObjectReference>(
                    "terminalFunctions.createMultiSessionTerminal",
                    terminalId,
                    sessionId.ToString(),
                    dotNetRef);

                _logger.LogDebug("[InitializeTerminal] ターミナル作成成功");
                return terminal;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InitializeTerminal] ターミナル作成エラー");
                return null;
            }
        }

        public async Task DestroyTerminalAsync(Guid sessionId, bool showAlert = true)
        {
            _logger.LogDebug("[DestroyTerminal] セッション {SessionId} のターミナルを破棄", sessionId);

            try
            {
                // JavaScript側のターミナルを破棄
                await _jsRuntime.InvokeVoidAsync("terminalFunctions.destroyTerminal", sessionId.ToString());
                _logger.LogDebug("[DestroyTerminal] JavaScript側のターミナル破棄完了");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DestroyTerminal] エラー");
                throw;
            }
        }

        public async Task RecreateTerminalAsync(Guid sessionId, SessionInfo sessionInfo, ConPtySession? activeSession)
        {
            _logger.LogDebug("[RecreateTerminal] セッション {SessionId} のターミナルを再作成", sessionId);

            try
            {
                // DOM更新と読み取りタスクの完全な終了を待つ
                await Task.Delay(TerminalConstants.SessionCreationDelay);

                // ターミナルdivの表示状態を確実に設定
                await EnsureTerminalVisibleAsync(sessionId);

                // ConPTYから自動的に画面状態が送信されるため、スナップショット送信は不要
                if (sessionInfo?.ConPtyBuffer != null && activeSession != null)
                {
                    await Task.Delay(100); // ターミナル初期化完了を待つ

                    // ターミナルに初期プロンプトを送信して接続を確認
                    await activeSession.WriteAsync("\r\n");
                    _logger.LogDebug("[RecreateTerminal] プロンプト送信完了");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RecreateTerminal] エラー");
                throw;
            }
        }

        public async Task HideAllTerminalsAsync()
        {
            _logger.LogDebug("[HideAllTerminals] すべてのターミナルを非表示に");
            try
            {
                await _jsRuntime.InvokeVoidAsync("terminalFunctions.hideAllTerminals");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[HideAllTerminals] ターミナル非表示エラー");
            }
        }

        public async Task ShowTerminalAsync(Guid sessionId)
        {
            _logger.LogDebug("[ShowTerminal] ターミナルを表示: {SessionId}", sessionId);
            try
            {
                await _jsRuntime.InvokeVoidAsync("terminalFunctions.showTerminal", sessionId.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ShowTerminal] ターミナル表示エラー");
            }
        }

        public async Task ResizeTerminalAsync(IJSObjectReference terminal)
        {
            try
            {
                await terminal.InvokeVoidAsync("resize");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[ResizeTerminal] リサイズエラー（無視）");
            }
        }

        public async Task ScrollToBottomAsync(Guid sessionId)
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("terminalFunctions.scrollToBottom", sessionId.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[ScrollToBottom] スクロールエラー（無視）");
            }
        }

        public async Task WriteToTerminalAsync(IJSObjectReference terminal, string data)
        {
            await terminal.InvokeVoidAsync("write", data);
        }

        public async Task<bool> CheckElementExistsAsync(string elementId)
        {
            try
            {
                return await _jsRuntime.InvokeAsync<bool>("terminalHubHelpers.checkElementExists", elementId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CheckElementExists] エラー");
                return false;
            }
        }

        public async Task EnsureTerminalVisibleAsync(Guid sessionId)
        {
            await _jsRuntime.InvokeVoidAsync("terminalFunctions.ensureTerminalVisible", sessionId.ToString());
        }
    }
}