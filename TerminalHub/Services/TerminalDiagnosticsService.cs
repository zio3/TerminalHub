using TerminalHub.Terminal;

namespace TerminalHub.Services
{
    /// <summary>
    /// ターミナル診断（「この画面を保存」ボタン・生出力リング・ブラウザ側イベント履歴・/api/debug/raw-ring）の
    /// 有効状態をプロセス全体で 1 つ持つ。設定「特殊 > 開発ツール > ターミナル診断」の保存と起動時に
    /// <see cref="SetEnabled"/> され、<see cref="RawChunkRing.CaptureEnabled"/> を同期区間で更新する。
    /// </summary>
    /// <remarks>
    /// 設定ダイアログは Circuit ごとなので、別タブで切り替えた結果を全タブの Root に届けるために
    /// <see cref="Changed"/> を持つ（Root はこれを購読してボタン表示と JS 側の記録を合わせる）。
    /// 保存した Circuit の JS 呼び出しが待ちや例外で止まっても、サーバー側の有効状態は保存直後に確定する。
    /// </remarks>
    public interface ITerminalDiagnosticsService
    {
        bool Enabled { get; }

        /// <summary>有効状態を更新する。値が変わったときだけ <see cref="Changed"/> を発火する。</summary>
        void SetEnabled(bool enabled);

        /// <summary>有効状態が変わったとき（引数は新しい値）。購読側は自分の Circuit へ InvokeAsync で入り直すこと。</summary>
        event Action<bool>? Changed;
    }

    public sealed class TerminalDiagnosticsService : ITerminalDiagnosticsService
    {
        private readonly object _lock = new();
        private bool _enabled;

        public bool Enabled
        {
            get { lock (_lock) { return _enabled; } }
        }

        public event Action<bool>? Changed;

        public void SetEnabled(bool enabled)
        {
            lock (_lock)
            {
                // リングの全体スイッチは常に揃える（起動時の初回反映でも通る）
                RawChunkRing.CaptureEnabled = enabled;
                if (_enabled == enabled)
                {
                    return;
                }
                _enabled = enabled;
            }
            Changed?.Invoke(enabled);
        }
    }
}
