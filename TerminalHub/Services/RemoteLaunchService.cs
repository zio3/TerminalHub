using System.Text.RegularExpressions;
using TerminalHub.Models;

namespace TerminalHub.Services;

public interface IRemoteLaunchService
{
    Task<string?> LaunchRemoteControlAsync(Guid sessionId, int timeoutSeconds = 30);
}

public class RemoteLaunchService : IRemoteLaunchService
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<RemoteLaunchService> _logger;

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

    public async Task<string?> LaunchRemoteControlAsync(Guid sessionId, int timeoutSeconds = 30)
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

        _logger.LogInformation("[RemoteLaunch] セッション起動開始: {SessionId} ({Name})", sessionId, sessionInfo.GetDisplayName());

        try
        {
            // --remote-controlオプションを追加
            var options = new Dictionary<string, string>(sessionInfo.Options)
            {
                ["remote-control"] = "true"
            };

            // SessionManager経由で新しいConPTYセッションを起動
            var conPtySession = await _sessionManager.GetOrCreateRemoteControlSessionAsync(sessionId, options);
            if (conPtySession == null)
            {
                _logger.LogWarning("[RemoteLaunch] セッション作成失敗: {SessionId}", sessionId);
                return null;
            }

            // URL検知を待機
            var urlTcs = new TaskCompletionSource<string?>();
            var buffer = "";

            void OnDataReceived(object? sender, DataReceivedEventArgs e)
            {
                var clean = AnsiEscapePattern.Replace(e.Data, " ");
                buffer += clean;

                var match = RemoteControlUrlPattern.Match(buffer);
                if (match.Success)
                {
                    var url = match.Value.TrimEnd(')', ']', '}', '>');
                    _logger.LogInformation("[RemoteLaunch] URL検知: {Url}", url);
                    sessionInfo.RemoteControlUrl = url;
                    urlTcs.TrySetResult(url);
                }
            }

            void OnProcessExited(object? sender, EventArgs e)
            {
                _logger.LogWarning("[RemoteLaunch] プロセスが予期せず終了: {SessionId}", sessionId);
                urlTcs.TrySetResult(null);
            }

            conPtySession.DataReceived += OnDataReceived;
            conPtySession.ProcessExited += OnProcessExited;

            // タイムアウト付きでURL検知を待機
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            cts.Token.Register(() => urlTcs.TrySetResult(null));

            var url = await urlTcs.Task;

            // イベントハンドラー解除
            conPtySession.DataReceived -= OnDataReceived;
            conPtySession.ProcessExited -= OnProcessExited;

            if (url == null)
            {
                _logger.LogWarning("[RemoteLaunch] URL検知タイムアウト({Timeout}秒): {SessionId}", timeoutSeconds, sessionId);
            }

            return url;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RemoteLaunch] 起動エラー: {SessionId}", sessionId);
            return null;
        }
    }
}
