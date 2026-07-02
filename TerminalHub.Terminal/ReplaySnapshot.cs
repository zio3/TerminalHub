using System.Text;

namespace TerminalHub.Terminal;

/// <summary>
/// <see cref="ITerminalStateBuffer.BeginReplay"/> が返すリプレイ用スナップショット。
/// <see cref="Content"/> は開始時点の確定状態。開始以降に <c>Append</c> された出力は
/// <see cref="ITerminalStateBuffer.EndReplay"/> まで内部のテールに蓄積され、順序どおりに取り出せる。
/// これにより「スナップショット取得〜xterm 書き込み完了」の間に届いたライブ出力が
/// 消失・順序逆転することなく復元できる（セッション切替/リサイズ再同期のレース対策）。
/// </summary>
public sealed class ReplaySnapshot
{
    /// <summary>スナップショット時点の復元用出力（xterm へ最初に書き込む内容）。</summary>
    public string Content { get; }

    internal StringBuilder Tail { get; } = new();

    internal ReplaySnapshot(string content)
    {
        Content = content;
    }
}
