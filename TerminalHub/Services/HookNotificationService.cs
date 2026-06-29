using Microsoft.Extensions.Logging;
using TerminalHub.Models;

namespace TerminalHub.Services;

/// <summary>
/// Claude Code hook からの通知を処理するサービス
/// </summary>
public interface IHookNotificationService
{
    /// <summary>
    /// Hook 通知を処理する
    /// </summary>
    Task HandleHookNotificationAsync(HookNotification notification);

    /// <summary>
    /// 通知イベントを登録する
    /// </summary>
    event EventHandler<HookNotificationEventArgs>? OnHookNotification;
}

/// <summary>
/// Hook 通知イベント引数
/// </summary>
public class HookNotificationEventArgs : EventArgs
{
    public HookNotification Notification { get; }

    public HookNotificationEventArgs(HookNotification notification)
    {
        Notification = notification;
    }
}

/// <summary>
/// HookNotificationService の実装
/// </summary>
public class HookNotificationService : IHookNotificationService
{
    private readonly ILogger<HookNotificationService> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly IAppSettingsService _appSettingsService;
    private readonly ISessionTimerService _sessionTimerService;
    private readonly ISessionRepository _sessionRepository;

    public event EventHandler<HookNotificationEventArgs>? OnHookNotification;

    public HookNotificationService(
        ILogger<HookNotificationService> logger,
        ISessionManager sessionManager,
        IAppSettingsService appSettingsService,
        ISessionTimerService sessionTimerService,
        ISessionRepository sessionRepository)
    {
        _logger = logger;
        _sessionManager = sessionManager;
        _appSettingsService = appSettingsService;
        _sessionTimerService = sessionTimerService;
        _sessionRepository = sessionRepository;
    }

    public async Task HandleHookNotificationAsync(HookNotification notification)
    {
        var eventType = notification.GetEventType();
        if (eventType == null)
        {
            _logger.LogWarning("不明なイベント種類: {Event}", notification.Event);
            return;
        }

        _logger.LogInformation(
            "Hook通知を受信: Event={Event}, SessionId={SessionId}, AgentId={AgentId}, AgentType={AgentType}, Timestamp={Timestamp}",
            notification.Event,
            notification.SessionId,
            notification.AgentId,
            notification.AgentType,
            notification.Timestamp);

        // セッション情報を取得
        var session = _sessionManager.GetSessionInfo(notification.SessionId);
        if (session == null)
        {
            _logger.LogWarning("セッションが見つかりません: {SessionId}", notification.SessionId);
            return;
        }

        // イベント種類に応じた処理（ステータス更新を先に実行）
        switch (eventType)
        {
            case HookEventType.Stop:
                await HandleStopEventAsync(session, notification);
                break;

            case HookEventType.UserPromptSubmit:
                await HandleUserPromptSubmitEventAsync(session, notification);
                break;

            case HookEventType.Notification:
                await HandleNotificationEventAsync(session, notification);
                break;

            case HookEventType.SubagentStart:
                await HandleSubagentStartEventAsync(session, notification);
                break;

            case HookEventType.SubagentStop:
                await HandleSubagentStopEventAsync(session, notification);
                break;

            case HookEventType.PreCompact:
                // compact 開始 = 作業中入り
                session.IsCompacting = true;
                _logger.LogInformation("PreCompact イベント処理（compact中入り）: Session={SessionName}", session.GetDisplayName());
                break;

            case HookEventType.PostCompact:
                // compact 完了 = 作業可能に復帰
                session.IsCompacting = false;
                _logger.LogInformation("PostCompact イベント処理（compact完了）: Session={SessionName}", session.GetDisplayName());
                break;

            case HookEventType.PreToolUse:
                // AskUserQuestion のみに絞って登録しているため、ここに来る = ユーザーへの質問が出た（回答待ち）。
                // ベル表示（非アクティブ時の気づき）は Root.razor 側で行う。ここはログのみ。
                _logger.LogInformation(
                    "PreToolUse イベント処理（ツール={ToolName}、回答待ち）: Session={SessionName}",
                    notification.ToolName, session.GetDisplayName());
                break;
        }

        // Hook イベントログに記録（何が来たか・処理後のサブエージェント数・message・tool_name。診断用）
        session.RecordHookEvent(notification.Event, notification.AgentId, notification.AgentType, session.RunningSubagentCount, notification.Message, notification.ToolName);

        // イベントを発火（ステータス更新後にUIを更新させる）
        OnHookNotification?.Invoke(this, new HookNotificationEventArgs(notification));
    }

    private async Task HandleStopEventAsync(SessionInfo session, HookNotification notification)
    {
        _logger.LogInformation("Stop イベント処理: Session={SessionName}", session.GetDisplayName());

        // タイムアウトタイマーを停止（SessionTimeoutを防ぐ）
        _sessionTimerService.StopSessionTimer(session.SessionId);

        // 処理時間を計算
        var elapsedSeconds = 0;
        if (session.ProcessingStartTime.HasValue)
        {
            elapsedSeconds = (int)(DateTime.Now - session.ProcessingStartTime.Value).TotalSeconds;
        }

        // Webhook通知を送信
        try
        {
            await _appSettingsService.SendWebhookAsync(
                "complete",
                SessionWebhookKey(session.SessionId),
                session.GetDisplayName(),
                session.TerminalType.ToString(),
                elapsedSeconds,
                session.FolderPath);
            _logger.LogInformation("処理完了通知を送信: Session={SessionName}, ElapsedSeconds={ElapsedSeconds}",
                session.GetDisplayName(), elapsedSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "処理完了通知の送信に失敗: Session={SessionName}", session.GetDisplayName());
        }

        // 最終利用時刻を更新（ソート用）
        _logger.LogInformation("[LastAccessedAt更新] きっかけ: HookNotificationService(Stop イベント), セッション: {SessionName}", session.GetDisplayName());
        session.LastAccessedAt = DateTime.Now;
        try { await _sessionRepository.SaveSessionAsync(session); } catch { }

        // 処理状態を完全にリセット（SessionTimeoutと同じ項目をクリア）
        _logger.LogInformation(
            "[ステータスクリア] きっかけ: HookNotificationService(Stop イベント), セッション: {SessionName}, 旧ステータス: {OldStatus}",
            session.GetDisplayName(),
            session.ProcessingStatus ?? "(なし)");

        // Stop イベント時刻を記録（OutputAnalyzerからの更新を一時的にスキップするため）
        session.LastStopEventTime = DateTime.Now;

        session.ProcessingStartTime = null;
        session.ProcessingStatus = null;
        session.ProcessingElapsedSeconds = null;
        session.LastProcessingUpdateTime = null;
        session.IsWaitingForUserInput = false;
    }

    /// <summary>
    /// SubagentStart: サブエージェントを agent_id で実行中リストに登録し、
    /// agent_id をキーに "start" Webhook を送る（個別 LED で稼働可視化するため）。
    /// </summary>
    private async Task HandleSubagentStartEventAsync(SessionInfo session, HookNotification notification)
    {
        if (!string.IsNullOrEmpty(notification.AgentId))
        {
            session.AddRunningSubagent(notification.AgentId, notification.AgentType);
        }
        else
        {
            _logger.LogWarning("SubagentStart に agent_id がありません: Session={SessionName}", session.GetDisplayName());
        }

        _logger.LogInformation(
            "SubagentStart イベント処理: Session={SessionName}, AgentId={AgentId}, AgentType={AgentType}, RunningCount={Count}",
            session.GetDisplayName(), notification.AgentId, notification.AgentType, session.RunningSubagentCount);

        await SendSubagentWebhookAsync(session, notification, "start", null);
    }

    /// <summary>
    /// SubagentStop: SubagentStart と同じ agent_id を実行中リストから除去し、
    /// agent_id をキーに "complete" Webhook を送る（個別 LED を消灯させるため）。
    /// </summary>
    private async Task HandleSubagentStopEventAsync(SessionInfo session, HookNotification notification)
    {
        var removed = false;
        if (!string.IsNullOrEmpty(notification.AgentId))
        {
            removed = session.RemoveRunningSubagent(notification.AgentId);
        }

        _logger.LogInformation(
            "SubagentStop イベント処理: Session={SessionName}, AgentId={AgentId}, AgentType={AgentType}, Removed={Removed}, RunningCount={Count}",
            session.GetDisplayName(), notification.AgentId, notification.AgentType, removed, session.RunningSubagentCount);

        await SendSubagentWebhookAsync(session, notification, "complete", null);
    }

    /// <summary>
    /// サブエージェント用 Webhook を送信する。session_id の代わりに agent_id を渡すことで、
    /// 受信側（LED 等）が各サブエージェントを個別のキーとして扱えるようにする。
    /// </summary>
    private async Task SendSubagentWebhookAsync(
        SessionInfo session, HookNotification notification, string eventType, int? elapsedSeconds)
    {
        // agent_id が無いと個別キーにできないため送らない
        if (string.IsNullOrEmpty(notification.AgentId))
        {
            return;
        }

        var name = string.IsNullOrEmpty(notification.AgentType)
            ? $"{session.GetDisplayName()} / subagent"
            : $"{session.GetDisplayName()} / {notification.AgentType}";

        try
        {
            await _appSettingsService.SendWebhookAsync(
                eventType,
                SubAgentWebhookKey(notification.AgentId),  // session_id の代わりに agent_id を SubAgent_ プレフィックス付きでキーにする
                name,
                session.TerminalType.ToString(),
                elapsedSeconds,
                session.FolderPath);
            _logger.LogInformation(
                "サブエージェント Webhook 送信: Event={Event}, AgentId={AgentId}, AgentType={AgentType}",
                eventType, notification.AgentId, notification.AgentType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "サブエージェント Webhook 送信に失敗: Event={Event}, AgentId={AgentId}", eventType, notification.AgentId);
        }
    }

    // Webhook の sessionId フィールドに種別プレフィックスを付ける。
    // 受信側（LED/MQTT）で生 GUID 同士が並ぶとセッションとサブエージェントの区別がつかないため、
    // "Session_" / "SubAgent_" を前置して個別キーとして判別できるようにする。
    private const string SessionKeyPrefix = "Session_";
    private const string SubAgentKeyPrefix = "SubAgent_";

    private static string SessionWebhookKey(Guid sessionId) => $"{SessionKeyPrefix}{sessionId}";
    private static string SubAgentWebhookKey(string agentId) => $"{SubAgentKeyPrefix}{agentId}";

    private async Task HandleUserPromptSubmitEventAsync(SessionInfo session, HookNotification notification)
    {
        _logger.LogInformation("UserPromptSubmit イベント処理: Session={SessionName}", session.GetDisplayName());

        // 注意: ここでサブエージェント集合をクリアしてはいけない。
        // サブエージェント走行中でも新しいプロンプトは送信でき、UserPromptSubmit は
        // 「全サブエージェント終了」を意味しない。クリアすると生きているカウントを
        // 0 にしてしまい、後続の SubagentStop が空振りする（実機ログで確認済み）。
        // 取りこぼしは agent_id による SubagentStop の突き合わせに任せる。

        // compact 中フラグは PostCompact 取りこぼしの保険として新ターン開始で必ず倒す。
        session.IsCompacting = false;

        // 処理開始を記録
        _logger.LogInformation(
            "[処理開始] きっかけ: HookNotificationService(UserPromptSubmit イベント), セッション: {SessionName}",
            session.GetDisplayName());

        // Stop イベントのクールダウンをリセット（新しい処理開始のため）
        session.LastStopEventTime = null;

        session.ProcessingStartTime = DateTime.Now;
        // ProcessingStatus は OutputAnalyzer が実際のステータステキストを設定するため、ここでは設定しない

        // Webhook通知を送信
        try
        {
            await _appSettingsService.SendWebhookAsync(
                "start",
                SessionWebhookKey(session.SessionId),
                session.GetDisplayName(),
                session.TerminalType.ToString(),
                null,
                session.FolderPath);
            _logger.LogInformation("処理開始通知を送信: Session={SessionName}", session.GetDisplayName());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "処理開始通知の送信に失敗: Session={SessionName}", session.GetDisplayName());
        }
    }

    private Task HandleNotificationEventAsync(SessionInfo session, HookNotification notification)
    {
        // Notification は許可待ち(permission_prompt)・アイドル(idle_prompt)・認証成功 等で発火。
        // 【検証フェーズ】message に何が入るか（許可待ち判別できるか）を Hook ログ＋このログで確認する。
        _logger.LogInformation(
            "Notification イベント処理: Session={SessionName}, Message={Message}",
            session.GetDisplayName(), notification.Message);
        return Task.CompletedTask;
    }
}
