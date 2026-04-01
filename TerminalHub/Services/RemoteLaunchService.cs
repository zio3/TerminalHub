using System.Text;
using System.Text.RegularExpressions;
using TerminalHub.Models;

namespace TerminalHub.Services;

public interface IRemoteLaunchService
{
    Task<string?> LaunchRemoteControlAsync(Guid sessionId, int timeoutSeconds = 60);
}

public class RemoteLaunchService : IRemoteLaunchService
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<RemoteLaunchService> _logger;

    /// <summary>リモート起動したConPTYセッションを保持（GC防止）</summary>
    private readonly Dictionary<Guid, ConPtySession> _activeRemoteSessions = new();

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

        // ログファイル準備
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs", "remote-launch");
        Directory.CreateDirectory(logsDir);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var logPath = Path.Combine(logsDir, $"launch-{timestamp}-{sessionId.ToString()[..8]}.log");
        var rawBuffer = new StringBuilder();

        _logger.LogInformation("[RemoteLaunch] ConPTY出力ログ: {LogPath}", logPath);

        try
        {
            // 既存オプションを引き継ぎ、--remote-controlを追加（extra-argsは除外）
            var options = new Dictionary<string, string>(sessionInfo.Options)
            {
                ["remote-control"] = "true"
            };
            options.Remove("extra-args");
            options.Remove("chrome");

            _logger.LogInformation("[RemoteLaunch] [状態: CREATING_SESSION] オプション: {Options}",
                string.Join(", ", options.Select(kv => $"{kv.Key}={kv.Value}")));

            // SessionManager経由で新しいConPTYセッションを起動
            var conPtySession = await _sessionManager.GetOrCreateRemoteControlSessionAsync(sessionId, options);
            if (conPtySession == null)
            {
                _logger.LogWarning("[RemoteLaunch] [状態: FAILED] セッション作成失敗: {SessionId}", sessionId);
                return null;
            }

            // セッションを保持（GC防止）
            _activeRemoteSessions[sessionId] = conPtySession;
            _logger.LogInformation("[RemoteLaunch] [状態: WAITING_URL] ConPTYセッション作成完了、URL検知待機中...（アクティブ: {Count}件）", _activeRemoteSessions.Count);

            // URL検知を待機
            var urlTcs = new TaskCompletionSource<string?>();
            var cleanBuffer = "";

            void OnDataReceived(object? sender, DataReceivedEventArgs e)
            {
                // 生データをバッファに蓄積
                rawBuffer.Append(e.Data);

                var clean = AnsiEscapePattern.Replace(e.Data, " ");
                cleanBuffer += clean;

                // クリーンデータの断片をログに出力（長い場合は先頭100文字）
                var fragment = clean.Length > 100 ? clean[..100] + "..." : clean;
                if (!string.IsNullOrWhiteSpace(fragment))
                {
                    _logger.LogDebug("[RemoteLaunch] [出力] {Fragment}", fragment.Trim());
                }

                var match = RemoteControlUrlPattern.Match(cleanBuffer);
                if (match.Success)
                {
                    var url = match.Value.TrimEnd(')', ']', '}', '>');
                    _logger.LogInformation("[RemoteLaunch] [状態: URL_DETECTED] URL検知: {Url}", url);
                    sessionInfo.RemoteControlUrl = url;
                    urlTcs.TrySetResult(url);
                }
            }

            void OnProcessExited(object? sender, EventArgs e)
            {
                _logger.LogWarning("[RemoteLaunch] [状態: PROCESS_EXITED] プロセスが予期せず終了: {SessionId}", sessionId);
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

            var url = await urlTcs.Task;

            // イベントハンドラー解除
            conPtySession.DataReceived -= OnDataReceived;
            conPtySession.ProcessExited -= OnProcessExited;

            // 生データをファイルに書き出し
            try
            {
                await File.WriteAllTextAsync(logPath, rawBuffer.ToString());
                _logger.LogInformation("[RemoteLaunch] ConPTY出力ログ書き込み完了: {Size}bytes", rawBuffer.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RemoteLaunch] ログファイル書き込み失敗");
            }

            if (url == null)
            {
                _logger.LogWarning("[RemoteLaunch] [状態: FAILED] URL検知タイムアウト({Timeout}秒): {SessionId}", timeoutSeconds, sessionId);
                // クリーンバッファの末尾をログに出力（デバッグ用）
                var tail = cleanBuffer.Length > 500 ? cleanBuffer[^500..] : cleanBuffer;
                _logger.LogWarning("[RemoteLaunch] クリーンバッファ末尾: {Tail}", tail);
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

            // エラー時もバッファをファイルに書き出し
            try
            {
                await File.WriteAllTextAsync(logPath, rawBuffer.ToString());
            }
            catch { }

            return null;
        }
    }
}
