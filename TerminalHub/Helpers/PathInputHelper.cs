namespace TerminalHub.Helpers
{
    /// <summary>
    /// フォルダパス入力欄の正規化。
    ///
    /// エクスプローラーの「パスのコピー」やターミナルからの貼り付けでは、パスが前後を
    /// ダブルクォートで括られた形（"C:\path\x"）になることがある。これをそのまま扱うと
    /// 先頭の <c>"</c> のせいで <see cref="System.IO.Directory.Exists(string)"/> が false になり、
    /// さらに相対パス扱いでアプリのベースディレクトリに連結されて不正パスで失敗する
    /// （例: ...\Programs\TerminalHub\"C:\Users\...\"）。入力段階でクォートを剥がして防ぐ。
    /// </summary>
    public static class PathInputHelper
    {
        /// <summary>
        /// 前後の空白を除き、対で括るクォート（" または '）が付いていれば1組だけ剥がす。
        /// 剥がした後も前後の空白を除く（" C:\x " のようなケース対応）。
        /// null/空白のみは空文字を返す。
        /// </summary>
        public static string NormalizeFolderInput(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var s = input.Trim();

            // 前後が対で括られている場合のみ剥がす（片側だけのクォートは触らない）
            if (s.Length >= 2 &&
                ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            {
                s = s.Substring(1, s.Length - 2).Trim();
            }

            return s;
        }
    }
}
