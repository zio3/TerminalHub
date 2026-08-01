namespace TerminalHub.Services;

/// <summary>
/// send_to_session の本文検査（純ロジック・テスト対象）。
///
/// 本文中の制御文字は**エンベロープ迂回**に使える: \r は受け手の TUI に送信確定として
/// 解釈されるため、"指示\r" で指示だけが先に無印で実行され、末尾のエンベロープは次の
/// 入力欄の残骸になる（「マーカー無し＝人間の指示」の境界をすり抜ける）。ESC は
/// エスケープシーケンス＝キー操作の注入、タブは補完等の UI 操作を起こすため、
/// C0 全域（U+0000〜U+001F）と DEL（U+007F）を一括で拒否する。
/// </summary>
public static class MessageTextGuard
{
    /// <summary>本文に制御文字が含まれていれば true を返し、最初の1文字を found に入れる。</summary>
    public static bool TryFindControlChar(string? message, out char found)
    {
        if (!string.IsNullOrEmpty(message))
        {
            foreach (var c in message)
            {
                if (c < ' ' || c == '\x7f')
                {
                    found = c;
                    return true;
                }
            }
        }

        found = default;
        return false;
    }
}
