namespace TerminalHub.Services;

/// <summary>
/// OutputAnalyzer の「クールダウン」境界判定を集約した純ロジック。
///
/// hook 駆動 CLI（ClaudeCode / CodexCLI）では、hook が立てたイベント直後に
/// 遅延した古い出力チャンクが解析されると、開いているプロンプトの待ちを誤クリアしたり
/// 完了直後のステータスを再設定したりするレースが起きる。これを避けるため、
/// 直近イベントから一定秒数はステータス更新/待ち解除をスキップする。
///
/// 時刻依存の境界ロジックは単体テストしにくいので、時刻を引数で受ける純関数として切り出し、
/// OutputAnalyzerService から呼ぶ（UI・ConPTY 非依存でヘッドレステスト可能）。
/// </summary>
public static class ProcessingCooldownPolicy
{
    /// <summary>
    /// Stop イベント後、この秒数間は OutputAnalyzer からのステータス更新をスキップ。
    /// 遅延した出力によるステータス再設定を防ぐ。
    /// </summary>
    public const double StopEventCooldownSeconds = 3.0;

    /// <summary>
    /// hook が入力待ちを立てた後、この秒数間は「処理中検出」による待ち解除をスキップ。
    /// プロンプト表示直前の古いスピナー出力チャンクが遅れて解析されて誤クリアするレースを避ける。
    /// 承認後の作業中に waiting が残る over-stay の解除は、ユーザー操作を挟む＝この窓より後に
    /// なるので影響しない。
    /// </summary>
    public const double WaitingHookCooldownSeconds = 1.5;

    /// <summary>
    /// 直近イベント時刻 <paramref name="lastEventTime"/> から <paramref name="now"/> までの経過が
    /// <paramref name="cooldownSeconds"/> 未満なら true（＝クールダウン内なのでスキップすべき）。
    /// イベント未発生（null）なら常に false。境界値（ちょうど cooldownSeconds 経過）は false。
    /// </summary>
    public static bool IsWithinCooldown(DateTime? lastEventTime, DateTime now, double cooldownSeconds)
    {
        if (!lastEventTime.HasValue)
        {
            return false;
        }

        return (now - lastEventTime.Value).TotalSeconds < cooldownSeconds;
    }
}
