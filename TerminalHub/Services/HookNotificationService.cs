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
    public bool PermissionRequestRequiresUserInput { get; }

    public HookNotificationEventArgs(
        HookNotification notification,
        bool permissionRequestRequiresUserInput)
    {
        Notification = notification;
        PermissionRequestRequiresUserInput = permissionRequestRequiresUserInput;
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

        var permissionRequestRequiresUserInput = eventType != HookEventType.PermissionRequest ||
            CodexProcessOptionsSnapshot.ResolvePermissionRequestRequiresUserInput(
                session.RunningCodexOptions, session.Options);

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
                await HandlePreCompactEventAsync(session, notification);
                break;

            case HookEventType.PostCompact:
                await HandlePostCompactEventAsync(session, notification);
                break;

            case HookEventType.PreToolUse:
                await HandlePreToolUseEventAsync(session, notification);
                break;

            case HookEventType.PermissionRequest:
                await HandlePermissionRequestEventAsync(
                    session, notification, permissionRequestRequiresUserInput);
                break;
        }

        // Hook イベントログに記録（何が来たか・処理後のサブエージェント数・message・tool_name。診断用）
        session.RecordHookEvent(notification.Event, notification.AgentId, notification.AgentType, session.RunningSubagentCount, notification.Message, notification.ToolName);

        // イベントを発火（ステータス更新後にUIを更新させる）
        OnHookNotification?.Invoke(
            this,
            new HookNotificationEventArgs(notification, permissionRequestRequiresUserInput));
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

        // Webhook通知を送信（本来の hook イベント名 "Stop" をそのまま eventType として送る。
        // 「Stop = LED 消灯」等の解釈は受け側に委ねる）。
        // 投げっぱなし（await しない）＝ Webhook 先が遅くても後続の UI 通知(OnHookNotification)や
        // hook 応答をブロックしない。
        FireWebhook(new WebhookPayload
        {
            EventType = notification.Event,
            SessionId = session.SessionId,
            SessionName = session.GetDisplayName(),
            TerminalType = session.TerminalType.ToString(),
            ElapsedSeconds = elapsedSeconds,
            FolderPath = session.FolderPath,
            Tool = SourceToolName(session)
        });
        _logger.LogInformation("Stop Webhook を送信(投げっぱなし): Session={SessionName}, ElapsedSeconds={ElapsedSeconds}",
            session.GetDisplayName(), elapsedSeconds);

        // 最終利用時刻を更新（ソート用）
        _logger.LogInformation("[LastAccessedAt更新] きっかけ: HookNotificationService(Stop イベント), セッション: {SessionName}", session.GetDisplayName());
        session.LastAccessedAt = DateTime.Now;
        try
        {
            await _sessionRepository.SaveSessionAsync(session);
        }
        catch (Exception ex)
        {
            // 保存失敗を黙殺すると並び順(LastAccessedAt)や状態復元の不具合を追えなくなるため、警告として残す
            _logger.LogWarning(ex, "Stop イベント後のセッション保存に失敗: {SessionName}", session.GetDisplayName());
        }

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
        session.ClearWaitingForUserInput();

        // compact 中フラグを倒す保険。auto-compact がブロック/中断され PostCompact が
        // 飛ばないケースがあり（公式仕様上、PreCompact がブロックされると PostCompact は来ない）、
        // その場合 IsCompacting が立ったままになる。Stop（＝ターン完了）でも必ず倒して、
        // 「コンパクト中」バッジの出っぱなしと「完了 + コンパクト中」の同時表示を防ぐ。
        // （従来は UserPromptSubmit でしか倒しておらず、次プロンプトまで残っていた）
        session.IsCompacting = false;
    }

    /// <summary>
    /// PreCompact: compact 中フラグを立て、本来の hook イベント名 "PreCompact" を
    /// そのまま Webhook で送る。LED の点灯（UserPromptSubmit と同じくスタート扱いにする等）は
    /// 受け側で解釈する。TerminalHub 側では start/complete へのマッピングや状態制御はしない。
    /// </summary>
    private async Task HandlePreCompactEventAsync(SessionInfo session, HookNotification notification)
    {
        session.IsCompacting = true;
        await SendSessionHookWebhookAsync(session, notification);
        _logger.LogInformation("PreCompact イベント処理（compact中入り）: Session={SessionName}", session.GetDisplayName());
    }

    /// <summary>
    /// PostCompact: compact 中フラグを倒し、本来の hook イベント名 "PostCompact" を
    /// そのまま Webhook で送る。LED の消灯等の解釈は受け側に委ねる。
    /// </summary>
    private async Task HandlePostCompactEventAsync(SessionInfo session, HookNotification notification)
    {
        session.IsCompacting = false;
        await SendSessionHookWebhookAsync(session, notification);
        _logger.LogInformation("PostCompact イベント処理（compact完了）: Session={SessionName}", session.GetDisplayName());
    }

    /// <summary>
    /// Webhook を投げっぱなし(fire-and-forget)で送信する。外部エンドポイントの応答遅延
    /// （例: Azure Functions のコールドスタートで 10 秒超）で hook 処理や UI 通知
    /// (OnHookNotification) がブロックされないよう、あえて await しない。HTTP 応答(204)を
    /// CLI に返した後もバックグラウンドで送信は継続する。全サービスが Singleton＋HttpClientFactory
    /// のためスコープ破棄の影響を受けず、SendWebhookAsync は内部で全例外を握り潰すため
    /// 未観測例外にもならない。
    /// </summary>
    private void FireWebhook(WebhookPayload payload)
    {
        _ = _appSettingsService.SendWebhookAsync(payload);
    }

    /// <summary>
    /// hook イベント名（notification.Event）をそのまま eventType として、セッションキーで Webhook を送る。
    /// start/complete へのマッピングをせず「本来のイベント」を流すための共通処理。
    /// </summary>
    private Task SendSessionHookWebhookAsync(SessionInfo session, HookNotification notification)
    {
        // 投げっぱなし。呼び出し元は従来どおり await できるよう即完了 Task を返す。
        FireWebhook(new WebhookPayload
        {
            EventType = notification.Event,
            SessionId = session.SessionId,
            SessionName = session.GetDisplayName(),
            TerminalType = session.TerminalType.ToString(),
            FolderPath = session.FolderPath,
            Tool = SourceToolName(session),
            Message = notification.Message,    // Notification 本文（あれば）
            ToolName = notification.ToolName   // PreToolUse の対象ツール名（あれば）
        });
        _logger.LogInformation("Hook Webhook を送信(投げっぱなし): Event={Event}, Session={SessionName}",
            notification.Event, session.GetDisplayName());
        return Task.CompletedTask;
    }

    /// <summary>
    /// SubagentStart: サブエージェントを agent_id で実行中リストに登録し、
    /// agent_id をキーに本来の hook イベント名 "SubagentStart" を Webhook で送る（個別 LED 用）。
    /// ただし agent_type 空の「意図しない発火」は冒頭でスキップし、登録も Webhook 送信も行わない
    /// （詳細は <see cref="IsUnintendedSubagentEvent"/>）。
    /// </summary>
    private async Task HandleSubagentStartEventAsync(SessionInfo session, HookNotification notification)
    {
        // agent_type が無い SubagentStart/Stop は「意図しない発火」としてスキップする。
        // 本物のユーザー Task サブエージェントは agent_type が入る（Explore / general-purpose 等）。
        // 一方 agent_type 空のものは SubagentStart を伴わず Stop だけ単発で飛ぶ等、Claude Code 内部処理
        // （recap 生成等）由来とみられる挙動が実機で確認されたため、稼働中カウントも Webhook も行わない
        // （個別 LED のノイズ・空振りを防ぐ）。診断用の Hook イベントログには引き続き記録される。
        if (IsUnintendedSubagentEvent(notification))
        {
            _logger.LogDebug(
                "SubagentStart（意図しない発火: agent_type無し）をスキップ: Session={SessionName}, AgentId={AgentId}",
                session.GetDisplayName(), notification.AgentId);
            return;
        }

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

        await SendSubagentWebhookAsync(session, notification);
    }

    /// <summary>
    /// agent_type を持たない Subagent 系イベントは「意図しない発火」（Claude Code 内部処理由来とみられる）。
    /// 本物のユーザー Task サブエージェントは agent_type（Explore 等）が必ず入る、という実機観測に基づく判定。
    /// </summary>
    private static bool IsUnintendedSubagentEvent(HookNotification notification)
        => string.IsNullOrEmpty(notification.AgentType);

    /// <summary>
    /// SubagentStop: SubagentStart と同じ agent_id を実行中リストから除去し、
    /// agent_id をキーに本来の hook イベント名 "SubagentStop" を Webhook で送る（個別 LED を消灯）。
    /// ただし agent_type 空の「意図しない発火」は冒頭でスキップし、除去も Webhook 送信も行わない
    /// （詳細は <see cref="IsUnintendedSubagentEvent"/>）。
    /// </summary>
    private async Task HandleSubagentStopEventAsync(SessionInfo session, HookNotification notification)
    {
        // agent_type 無しは「意図しない発火」としてスキップ（理由は SubagentStart 側コメント参照）。
        if (IsUnintendedSubagentEvent(notification))
        {
            _logger.LogDebug(
                "SubagentStop（意図しない発火: agent_type無し）をスキップ: Session={SessionName}, AgentId={AgentId}",
                session.GetDisplayName(), notification.AgentId);
            return;
        }

        var removed = false;
        if (!string.IsNullOrEmpty(notification.AgentId))
        {
            removed = session.RemoveRunningSubagent(notification.AgentId);
        }

        _logger.LogInformation(
            "SubagentStop イベント処理: Session={SessionName}, AgentId={AgentId}, AgentType={AgentType}, Removed={Removed}, RunningCount={Count}",
            session.GetDisplayName(), notification.AgentId, notification.AgentType, removed, session.RunningSubagentCount);

        await SendSubagentWebhookAsync(session, notification);
    }

    /// <summary>
    /// サブエージェント用 Webhook を送信する。session_id の代わりに agent_id を渡すことで、
    /// 受信側（LED 等）が各サブエージェントを個別のキーとして扱えるようにする。
    /// eventType は本来の hook イベント名（notification.Event）をそのまま送る。
    /// </summary>
    private Task SendSubagentWebhookAsync(SessionInfo session, HookNotification notification)
    {
        // agent_id が無いと個別キーにできないため送らない
        if (string.IsNullOrEmpty(notification.AgentId))
        {
            return Task.CompletedTask;
        }

        // 呼び出し元（SubagentStart/Stop ハンドラ）が agent_type 空をスキップ済みのため、
        // ここに到達する時点で AgentType は必ず非空。
        var name = $"{session.GetDisplayName()} / {notification.AgentType}";

        // 投げっぱなし。呼び出し元は従来どおり await できるよう即完了 Task を返す。
        FireWebhook(new WebhookPayload
        {
            EventType = notification.Event,
            SessionId = session.SessionId,        // 親セッションの GUID（生）
            SessionName = name,
            TerminalType = session.TerminalType.ToString(),
            ElapsedSeconds = null,                // サブエージェントの経過時間は追跡していない
            FolderPath = session.FolderPath,
            Tool = SourceToolName(session),
            AgentId = notification.AgentId        // サブエージェント ID（受信側で個別キーに使える）
        });
        _logger.LogInformation(
            "サブエージェント Webhook 送信(投げっぱなし): Event={Event}, AgentId={AgentId}, AgentType={AgentType}",
            notification.Event, notification.AgentId, notification.AgentType);
        return Task.CompletedTask;
    }

    // Webhook の tool フィールドに入れる送信元 CLI 名（スペース無し、受信側で扱いやすいように）。
    // セッションの種別から導出する（ClaudeCode→"ClaudeCode" / CodexCLI→"CodexCLI"）。
    // hook が飛ぶのは ClaudeCode / CodexCLI セッションのみ。
    private static string SourceToolName(SessionInfo session) => session.TerminalType.ToString();

    private async Task HandleUserPromptSubmitEventAsync(SessionInfo session, HookNotification notification)
    {
        // agent_id 付きの UserPromptSubmit はサブエージェントの内部プロンプト（Codex 特有。Claude では付かない）。
        // メインセッションの「開始」ではないので、メインの処理開始リセット・start Webhook はしない（誤発火防止）。
        if (!string.IsNullOrEmpty(notification.AgentId))
        {
            _logger.LogDebug(
                "UserPromptSubmit（サブエージェント内部 agent_id={AgentId}）はメイン開始扱いしないでスキップ: Session={SessionName}",
                notification.AgentId, session.GetDisplayName());
            return;
        }

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

        // 新しいプロンプトが送られた＝直前の許可/選択待ちは解消。入力待ちフラグを下ろす。
        session.ClearWaitingForUserInput();

        // Webhook通知を送信（本来の hook イベント名 "UserPromptSubmit" をそのまま eventType として送る。
        // 「UserPromptSubmit = LED 点灯」等の解釈は受け側に委ねる）。投げっぱなし（await しない）。
        FireWebhook(new WebhookPayload
        {
            EventType = notification.Event,
            SessionId = session.SessionId,
            SessionName = session.GetDisplayName(),
            TerminalType = session.TerminalType.ToString(),
            FolderPath = session.FolderPath,
            Tool = SourceToolName(session)
        });
        _logger.LogInformation("UserPromptSubmit Webhook を送信(投げっぱなし): Session={SessionName}", session.GetDisplayName());
    }

    /// <summary>
    /// Notification: 許可待ち(permission)・アイドル(idle)・認証成功 等で発火。本来の "Notification" を
    /// message 付きで Webhook 送信する（受信側で message を見て許可待ち/idle を区別し、LED の色等を変えられる）。
    /// </summary>
    private async Task HandleNotificationEventAsync(SessionInfo session, HookNotification notification)
    {
        _logger.LogInformation(
            "Notification イベント処理: Session={SessionName}, Message={Message}",
            session.GetDisplayName(), notification.Message);

        // message に "permission" を含めば許可待ち、それ以外は idle/認証成功 等の非permission通知。
        // 許可待ちフラグは立て下げを対称にする（idle 通知で解除しないと stuck するため）。
        if (!string.IsNullOrEmpty(notification.Message) &&
            notification.Message.Contains("permission", StringComparison.OrdinalIgnoreCase))
        {
            session.IsWaitingForPermission = true;
            session.LastWaitingHookSetTime = DateTime.Now;
        }
        else
        {
            // 非permission通知＝許可待ちではない。許可待ちフラグを解除する（選択待ちは PreToolUse 管理なので触らない）。
            session.IsWaitingForPermission = false;
        }

        await SendSessionHookWebhookAsync(session, notification);
    }

    /// <summary>
    /// PreToolUse: AskUserQuestion のみに絞って登録しているため、発火 = ユーザーへの質問（回答待ち）。
    /// 本来の "PreToolUse" を toolName 付きで Webhook 送信する（受信側で「質問待ち」として LED 色を変えられる）。
    /// ベル表示（非アクティブ時の気づき）は Root.razor 側で行う。
    /// </summary>
    private async Task HandlePreToolUseEventAsync(SessionInfo session, HookNotification notification)
    {
        _logger.LogInformation(
            "PreToolUse イベント処理（ツール={ToolName}、回答待ち）: Session={SessionName}",
            notification.ToolName, session.GetDisplayName());

        // 発火 = ユーザーへの質問（選択待ち）。選択待ちフラグを立てる（send_to_session の送信ガード / UI アイコン用）。
        session.IsWaitingForSelection = true;
        session.LastWaitingHookSetTime = DateTime.Now;

        await SendSessionHookWebhookAsync(session, notification);
    }

    /// <summary>
    /// PermissionRequest（Codex）: user reviewer の場合だけユーザー承認待ちとして扱い、
    /// toolName 付きで Webhook 送信する。auto_review の場合は内部履歴だけに記録し、
    /// 下流がユーザー待ちと誤認しないよう Webhook へ伝搬しない。ベル表示は Root.razor 側。
    /// hook 戻り値で承認制御はしない（観測のみ。ブリッジは空応答で Codex の通常承認フローを保つ）。
    /// </summary>
    private async Task HandlePermissionRequestEventAsync(
        SessionInfo session,
        HookNotification notification,
        bool requiresUserInput)
    {
        if (!requiresUserInput)
        {
            _logger.LogInformation(
                "PermissionRequest イベント処理（ツール={ToolName}、Auto-reviewへ委譲、Webhook抑制）: Session={SessionName}",
                notification.ToolName, session.GetDisplayName());

            // 設定変更前の許可待ちが残っていても、選択待ち状態は壊さず許可待ちだけ解除する。
            session.IsWaitingForPermission = false;
            if (!session.IsWaitingForSelection)
            {
                session.LastWaitingHookSetTime = null;
            }
            return;
        }

        _logger.LogInformation(
            "PermissionRequest イベント処理（ツール={ToolName}、承認待ち）: Session={SessionName}",
            notification.ToolName, session.GetDisplayName());

        // ツール実行の承認待ち。許可待ちフラグを立てる（send_to_session の送信ガード / UI アイコン用）。
        session.IsWaitingForPermission = true;
        session.LastWaitingHookSetTime = DateTime.Now;

        await SendSessionHookWebhookAsync(session, notification);
    }
}
