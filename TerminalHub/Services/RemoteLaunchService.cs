using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using TerminalHub.Models;

namespace TerminalHub.Services;

public interface IRemoteLaunchService
{
    Task<string?> LaunchRemoteControlAsync(Guid sessionId, int timeoutSeconds = 60);
    void DisconnectRemoteSession(Guid sessionId);
}

public class RemoteLaunchService : IRemoteLaunchService
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<RemoteLaunchService> _logger;

    /// <summary>セッションIDから最新のConPTYセッションを管理（GC防止兼ライフサイクル管理）</summary>
    private readonly ConcurrentDictionary<Guid, ConPtySession> _activeRemoteSessions = new();

    /// <summary>--continueエラーによるリトライを示す内部シグナル</summary>
    private const string ContinueErrorSentinel = "__CONTINUE_ERROR__";

    private static readonly Regex RemoteControlUrlPattern = new(
        @"https://claude\.ai/code/[a-zA-Z0-9\-_/]+",
        RegexOptions.Compiled);

    private static readonly Regex AnsiEscapePattern = new(
        @"\x1b\[[0-9;]*[a-zA-Z]|\x1b\][^\x07]*\x07|\x1b\[\?[0-9;]*[a-zA-Z]|\r",
        RegexOptions.Compiled);

    public RemoteLaunchService(
        ISessionManager sessionManager,
        ILogger<RemoteLaunchService> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public async Task<string?> LaunchRemoteControlAsync(Guid sessionId, int timeoutSeconds = 60)
    {
        var sessionInfo = _sessionManager.GetSessionInfo(sessionId);
        if (sessionInfo == null)
        {
            _logger.LogWarning("[RemoteLaunch] セッションが見つかりません: {SessionId}", sessionId);
            return null;
        }

        if (sessionInfo.TerminalType != TerminalType.ClaudeCode)
        {
            _logger.LogWarning("[RemoteLaunch] ClaudeCode以外のセッションは非対応: {SessionId} ({Type})", sessionId, sessionInfo.TerminalType);
            return null;
        }

        _logger.LogInformation("[RemoteLaunch] [状態: STARTING] セッション起動開始: {SessionId} ({Name})", sessionId, sessionInfo.GetDisplayName());

        try
        {
            var url = await LaunchAndWaitForUrlAsync(sessionId, sessionInfo, includeContinue: true, timeoutSeconds: timeoutSeconds);

            if (url == null)
            {
                _logger.LogWarning("[RemoteLaunch] [状態: FAILED] URL取得失敗: {SessionId}", sessionId);
            }
            else
            {
                _logger.LogInformation("[RemoteLaunch] [状態: SUCCESS] URL返却: {Url}", url);
            }

            return url;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RemoteLaunch] [状態: ERROR] 起動エラー: {SessionId}", sessionId);
            return null;
        }
    }

    /// <summary>
    /// ConPTYセッションを起動し、Remote Control URLの検知を待機する。
    /// --continueエラーが検出された場合は、--continueなしで自動リトライする。
    /// </summary>
    private async Task<string?> LaunchAndWaitForUrlAsync(Guid sessionId, SessionInfo sessionInfo, bool includeContinue, int timeoutSeconds)
    {
        // 既存オプションを引き継ぎ、--remote-controlを追加
        var options = new Dictionary<string, string>(sessionInfo.Options)
        {
            ["remote-control"] = "true"
        };
        // extra-argsはリモート起動時に除外（予期しない確認画面の回避）
        options.Remove("extra-args");

        // --continueを除外する場合
        if (!includeContinue)
        {
            options.Remove("continue");
        }

        _logger.LogInformation("[RemoteLaunch] [状態: CREATING_SESSION] オプション: {Options}",
            string.Join(", ", options.Select(kv => $"{kv.Key}={kv.Value}")));

        // SessionManager経由で新しいConPTYセッションを起動
        var conPtySession = await _sessionManager.GetOrCreateRemoteControlSessionAsync(sessionId, options);
        if (conPtySession == null)
        {
            _logger.LogWarning("[RemoteLaunch] [状態: FAILED] セッション作成失敗: {SessionId}", sessionId);
            return null;
        }

        // 前回のリモートセッションがあれば解放してから登録
        ReleaseExistingSession(sessionId);
        _activeRemoteSessions[sessionId] = conPtySession;
        _logger.LogInformation("[RemoteLaunch] [状態: WAITING_URL] ConPTYセッション作成完了、URL検知待機中...（アクティブ: {Count}件）", _activeRemoteSessions.Count);

        // URL検知を待機
        var urlTcs = new TaskCompletionSource<string?>();
        var cleanBuffer = "";
        var urlDetected = false;
        var continueErrorDetected = false;
        var hasContinueOption = includeContinue && options.ContainsKey("continue");

        void OnDataReceived(object? sender, DataReceivedEventArgs e)
        {
            if (urlDetected || continueErrorDetected) return;

            var clean = AnsiEscapePattern.Replace(e.Data, " ");
            cleanBuffer += clean;

            // --continueエラーを検出（セッション開始直後のみ）
            if (hasContinueOption && cleanBuffer.Contains("No conversation found to continue"))
            {
                continueErrorDetected = true;
                _logger.LogInformation("[RemoteLaunch] 'No conversation found to continue' エラーを検出。--continueなしでリトライします: {SessionId}", sessionId);

                conPtySession.DataReceived -= OnDataReceived;
                conPtySession.ProcessExited -= OnProcessExited;

                // リトライシグナルとして特別な値を返す
                urlTcs.TrySetResult(ContinueErrorSentinel);
                return;
            }

            var match = RemoteControlUrlPattern.Match(cleanBuffer);
            if (match.Success)
            {
                urlDetected = true;
                var url = match.Value.TrimEnd(')', ']', '}', '>');
                _logger.LogInformation("[RemoteLaunch] [状態: URL_DETECTED] URL検知: {Url}", url);
                sessionInfo.RemoteControlUrl = url;

                // URL検知後はイベントハンドラーを即座に解除
                conPtySession.DataReceived -= OnDataReceived;
                conPtySession.ProcessExited -= OnProcessExited;

                urlTcs.TrySetResult(url);
            }
        }

        void OnProcessExited(object? sender, EventArgs e)
        {
            _logger.LogWarning("[RemoteLaunch] [状態: PROCESS_EXITED] プロセスが予期せず終了: {SessionId}", sessionId);
            _activeRemoteSessions.TryRemove(sessionId, out _);
            urlTcs.TrySetResult(null);
        }

        conPtySession.DataReceived += OnDataReceived;
        conPtySession.ProcessExited += OnProcessExited;

        conPtySession.Start();
        _logger.LogInformation("[RemoteLaunch] [状態: PROCESS_STARTED] ConPTYプロセス開始");

        // タイムアウト付きでURL検知を待機
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        cts.Token.Register(() =>
        {
            _logger.LogWarning("[RemoteLaunch] [状態: TIMEOUT] {Timeout}秒経過", timeoutSeconds);
            urlTcs.TrySetResult(null);
        });

        var result = await urlTcs.Task;

        // タイムアウト時のイベントハンドラー解除
        if (!urlDetected && !continueErrorDetected)
        {
            conPtySession.DataReceived -= OnDataReceived;
            conPtySession.ProcessExited -= OnProcessExited;
        }

        // --continueエラーの場合、既存セッションを破棄して--continueなしでリトライ
        if (result == ContinueErrorSentinel)
        {
            ReleaseExistingSession(sessionId);
            return await LaunchAndWaitForUrlAsync(sessionId, sessionInfo, includeContinue: false, timeoutSeconds: timeoutSeconds);
        }

        if (result == null)
        {
            _logger.LogWarning("[RemoteLaunch] [状態: FAILED] URL検知タイムアウト({Timeout}秒): {SessionId}", timeoutSeconds, sessionId);
            var tail = cleanBuffer.Length > 500 ? cleanBuffer[^500..] : cleanBuffer;
            _logger.LogWarning("[RemoteLaunch] クリーンバッファ末尾: {Tail}", tail);
        }

        return result;
    }

    public void DisconnectRemoteSession(Guid sessionId)
    {
        if (_activeRemoteSessions.TryRemove(sessionId, out var session))
        {
            try
            {
                session.Dispose();
                _logger.LogInformation("[RemoteLaunch] リモートセッション切断: {SessionId}（残り: {Count}件）", sessionId, _activeRemoteSessions.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RemoteLaunch] リモートセッション切断エラー: {SessionId}", sessionId);
            }
        }
        else
        {
            _logger.LogWarning("[RemoteLaunch] 切断対象のリモートセッションが見つかりません: {SessionId}", sessionId);
        }
    }

    private void ReleaseExistingSession(Guid sessionId)
    {
        if (_activeRemoteSessions.TryRemove(sessionId, out var oldSession))
        {
            try
            {
                oldSession.Dispose();
                _logger.LogInformation("[RemoteLaunch] 既存リモートセッションを解放: {SessionId}", sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RemoteLaunch] 既存リモートセッション解放エラー: {SessionId}", sessionId);
            }
        }
    }
}
