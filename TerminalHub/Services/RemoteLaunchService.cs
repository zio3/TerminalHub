using System.Text;
using System.Text.RegularExpressions;
using TerminalHub.Models;

namespace TerminalHub.Services;

public interface IRemoteLaunchService
{
    /// <summary>
    /// 対象セッションから Remote Control URL を取得する。
    /// </summary>
    /// <returns>
    /// 成功時は URL、失敗時は null。null が返るパターンは以下:
    /// <list type="bullet">
    ///   <item>セッションが存在しない (<see cref="ISessionManager.GetSessionInfo"/> が null)</item>
    ///   <item>ターミナルタイプが ClaudeCode 以外</item>
    ///   <item>ConPty 取得失敗 (<see cref="ISessionManager.GetSessionAsync"/> が null)</item>
    ///   <item>Busy 状態 (<see cref="Models.SessionInfo.ProcessingStatus"/> が非空)</item>
    ///   <item>応答なし: <c>/remote-control</c> 送信後 1 秒経過しても URL も <c>connecting…</c> も検出されない</item>
    ///   <item>URL タイムアウト: <c>connecting…</c> 検出 → 接続完了待ち → 2 回目の <c>/remote-control</c> 送信後、URL を <paramref name="timeoutSeconds"/> 秒以内に検出できず</item>
    ///   <item>内部例外 (catch ブロック到達)</item>
    /// </list>
    /// 詳細は <c>docs/mqtt-api-spec.md</c> の「<c>launch failed or timeout</c> の内訳」節を参照。
    /// </returns>
    Task<string?> LaunchRemoteControlAsync(Guid sessionId, int timeoutSeconds = 60);

    /// <summary>
    /// SessionInfo の <see cref="Models.SessionInfo.RemoteControlUrl"/> 表示状態をクリアする。
    /// 新方式では既存セッションを使い回しているため、リモート接続自体は切らない (TUI で手動操作が必要)。
    /// </summary>
    void DisconnectRemoteSession(Guid sessionId);
}

/// <summary>
/// 起動済み（または遅延起動した）Claude Code セッションに対して /remote-control コマンドを送って
/// Remote Control の URL を取得する。
///
/// Claude Code v2.1.162 以降、--remote-control 起動時の URL は stdout の startup message に
/// 出力されなくなり、フッターピル (TUI) に格納される形式に変わったため、起動引数経由ではなく
/// 既存セッションへ /remote-control コマンドを送る方式に切替えている。
///
/// URL は TerminalHub 側にキャッシュせず、要求の都度セッションに問い合わせる方針。
///
/// フロー:
///   1. SessionManager.GetSessionAsync で ConPty を取得 (未起動なら遅延初期化)
///   2. SessionInfo.ProcessingStatus を見て Busy なら即 null
///   3. /remote-control を送信 → 1 秒待って応答を確認:
///      - 直接 URL が出る = 既に RC 接続済みだったので status panel が即時オープン → 完了
///      - "connecting…" が出る = 新規接続中 → 接続完了を待って 2 回目の /remote-control 送信
///   4. (必要なら) 2 回目の /remote-control を送り、status panel から URL を取り出す
///   5. Esc を送って panel を閉じる
///   6. URL を SessionInfo に保存 (UI 表示用) し返却
/// </summary>
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

    /// <summary>送信後、応答を見極めるために待つ固定時間。</summary>
    private static readonly TimeSpan ResponseInspectDelay = TimeSpan.FromSeconds(1);

    /// <summary>"connecting…" 検出後、接続完了を待つ固定時間。</summary>
    private static readonly TimeSpan ConnectingSettleDelay = TimeSpan.FromSeconds(3);

    /// <summary>URL を polling で探すときの interval。</summary>
    private static readonly TimeSpan UrlPollInterval = TimeSpan.FromMilliseconds(500);

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

        _logger.LogInformation("[RemoteLaunch] [状態: STARTING] セッション準備: {SessionId} ({Name})", sessionId, sessionInfo.GetDisplayName());

        try
        {
            // ConPty を取得 (未起動なら遅延初期化で起動される)
            var conPtySession = await _sessionManager.GetSessionAsync(sessionId);
            if (conPtySession == null)
            {
                _logger.LogWarning("[RemoteLaunch] [状態: FAILED] ConPty 取得失敗: {SessionId}", sessionId);
                return null;
            }

            // Busy 判定: OutputAnalyzer が処理中ステータスを立てていれば抜ける
            if (!string.IsNullOrEmpty(sessionInfo.ProcessingStatus))
            {
                _logger.LogWarning("[RemoteLaunch] [状態: BUSY] 処理中のため中止: {SessionId} (status={Status})", sessionId, sessionInfo.ProcessingStatus);
                return null;
            }

            var url = await RequestUrlViaRcCommandAsync(conPtySession, timeoutSeconds);

            if (url == null)
            {
                _logger.LogWarning("[RemoteLaunch] [状態: FAILED] URL 取得失敗: {SessionId}", sessionId);
            }
            else
            {
                sessionInfo.RemoteControlUrl = url;
                _logger.LogInformation("[RemoteLaunch] [状態: SUCCESS] URL 返却: {Url}", url);
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
    /// 既存 ConPty に /remote-control を送って URL を取り出す。
    /// 「送信 → 1 秒待つ → 応答文字列を見て分岐」方式。
    /// </summary>
    private async Task<string?> RequestUrlViaRcCommandAsync(ConPtySession conPtySession, int timeoutSeconds)
    {
        var buffer = new StringBuilder();
        var bufferLock = new object();

        void OnDataReceived(object? sender, DataReceivedEventArgs e)
        {
            var clean = AnsiEscapePattern.Replace(e.Data, " ");
            lock (bufferLock)
            {
                buffer.Append(clean);
            }
        }

        conPtySession.DataReceived += OnDataReceived;

        try
        {
            // フェーズ 1: /remote-control 送信 → 1 秒待って応答確認
            var phase1Offset = SnapshotBufferLength(buffer, bufferLock);
            _logger.LogInformation("[RemoteLaunch] [状態: SENDING_RC_1] /remote-control 1 回目送信");
            await conPtySession.WriteAsync("/remote-control\r");
            await Task.Delay(ResponseInspectDelay);

            var phase1Snap = GetBufferSlice(buffer, bufferLock, phase1Offset);

            // ケース B: URL が即出ている (起動時から接続済みだった)
            var directUrl = ExtractUrl(phase1Snap);
            if (directUrl != null)
            {
                _logger.LogInformation("[RemoteLaunch] [状態: URL_DIRECT] 1 回目で URL 検出（接続済みだった）: {Url}", directUrl);
                await TryCloseStatusPanelAsync(conPtySession);
                return directUrl;
            }

            // ケース A: "connecting" 検出 → 接続中なのでもう一度送る
            if (ContainsConnectingIndicator(phase1Snap))
            {
                _logger.LogInformation("[RemoteLaunch] [状態: CONNECTING_DETECTED] connecting… を検出、接続完了を待機");
                await Task.Delay(ConnectingSettleDelay);

                var phase2Offset = SnapshotBufferLength(buffer, bufferLock);
                _logger.LogInformation("[RemoteLaunch] [状態: SENDING_RC_2] /remote-control 2 回目送信");
                await conPtySession.WriteAsync("/remote-control\r");

                // URL を timeoutSeconds 秒まで polling
                var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(UrlPollInterval);
                    var snap2 = GetBufferSlice(buffer, bufferLock, phase2Offset);
                    var url2 = ExtractUrl(snap2);
                    if (url2 != null)
                    {
                        _logger.LogInformation("[RemoteLaunch] [状態: URL_AFTER_CONNECT] URL 取得: {Url}", url2);
                        await TryCloseStatusPanelAsync(conPtySession);
                        return url2;
                    }
                }

                _logger.LogWarning("[RemoteLaunch] [状態: TIMEOUT_PHASE2] 2 回目の /remote-control 後、URL を {Timeout} 秒以内に検出できず", timeoutSeconds);
                LogBufferTail(buffer, bufferLock);
                return null;
            }

            _logger.LogWarning("[RemoteLaunch] [状態: UNEXPECTED] {Delay} 秒経過しても URL/connecting どちらも検出できず", ResponseInspectDelay.TotalSeconds);
            LogBufferTail(buffer, bufferLock);
            return null;
        }
        finally
        {
            conPtySession.DataReceived -= OnDataReceived;
        }
    }

    private static int SnapshotBufferLength(StringBuilder buffer, object bufferLock)
    {
        lock (bufferLock)
        {
            return buffer.Length;
        }
    }

    private static string GetBufferSlice(StringBuilder buffer, object bufferLock, int offset)
    {
        lock (bufferLock)
        {
            if (buffer.Length <= offset) return string.Empty;
            return buffer.ToString(offset, buffer.Length - offset);
        }
    }

    private static string? ExtractUrl(string snapshot)
    {
        var matches = RemoteControlUrlPattern.Matches(snapshot);
        if (matches.Count == 0) return null;
        return matches[^1].Value.TrimEnd(')', ']', '}', '>');
    }

    private static bool ContainsConnectingIndicator(string snapshot)
    {
        return snapshot.Contains("connecting", StringComparison.OrdinalIgnoreCase);
    }

    private async Task TryCloseStatusPanelAsync(ConPtySession conPtySession)
    {
        try
        {
            await conPtySession.WriteAsync("\x1b");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RemoteLaunch] Esc 送信失敗（無視）");
        }
    }

    private void LogBufferTail(StringBuilder buffer, object bufferLock)
    {
        string content;
        lock (bufferLock)
        {
            content = buffer.ToString();
        }
        var tail = content.Length > 500 ? content[^500..] : content;
        _logger.LogWarning("[RemoteLaunch] クリーンバッファ末尾: {Tail}", tail);
    }

    public void DisconnectRemoteSession(Guid sessionId)
    {
        // 旧実装はリモート専用 ConPty を破棄していたが、新方式では既存セッションを使い回すので
        // ConPty には触らない。SessionInfo の URL 表示状態だけクリアする。
        var sessionInfo = _sessionManager.GetSessionInfo(sessionId);
        if (sessionInfo != null)
        {
            sessionInfo.RemoteControlUrl = null;
            _logger.LogInformation("[RemoteLaunch] RemoteControlUrl をクリア: {SessionId}", sessionId);
        }
        else
        {
            _logger.LogWarning("[RemoteLaunch] 切断対象のセッションが見つかりません: {SessionId}", sessionId);
        }
    }
}
