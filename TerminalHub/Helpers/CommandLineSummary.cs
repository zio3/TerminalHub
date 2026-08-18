using System.Text.RegularExpressions;

namespace TerminalHub.Helpers
{
    /// <summary>
    /// 起動コマンドラインをコンソール表示用に短くする。
    ///
    /// Codex の lifecycle hook は起動引数として注入するため（CodexHookService）、
    /// <c>-c "hooks.&lt;イベント名&gt;=..."</c> が8個並ぶ。値には PowerShell の
    /// -EncodedCommand の Base64 が丸ごと入るので、1セッション分で数千文字になり、
    /// コンソールに出すと他のログが流れて読めなくなる。
    ///
    /// hook の引数だけを畳み、それ以外（--sandbox 等の起動オプションや作業フォルダ）は
    /// そのまま残す。起動オプションの確認はコンソールのままできる、というのが狙い。
    /// 全文はファイルログに残す。
    /// </summary>
    public static class CommandLineSummary
    {
        // -c "hooks.Stop=[...]" 形式。値の中では引用符に ' を使うので、閉じの " は曖昧にならない
        private static readonly Regex HookArgRegex = new(
            "-c \"hooks\\.[^\"]*\"",
            RegexOptions.Compiled);

        /// <summary>
        /// hook 注入の引数を「&lt;hooks 引数 N 件を省略: M 文字&gt;」に畳んだものを返す。
        /// 対象が無ければ元の文字列をそのまま返す。
        /// </summary>
        public static string FoldHookArguments(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine))
                return commandLine;

            var matches = HookArgRegex.Matches(commandLine);
            if (matches.Count == 0)
                return commandLine;

            var omittedChars = matches.Sum(m => m.Length);
            var placeholder = $"<hooks 引数 {matches.Count} 件を省略: {omittedChars:N0} 文字>";

            // hook の引数は連続して並ぶ（CodexHookService が続けて足す）ので、
            // 最初から最後までを丸ごと1つの目印に置き換える。
            // こうすると前後の区切り空白がそのまま活きるため、空白の詰め直しが要らない。
            var first = matches[0];
            var last = matches[^1];
            if (IsOnlyWhitespaceBetween(commandLine, matches))
            {
                var tailStart = last.Index + last.Length;
                return string.Concat(
                    commandLine.AsSpan(0, first.Index),
                    placeholder,
                    commandLine.AsSpan(tailStart));
            }

            // 連続していない場合（現状の実装では起きない）は、間に挟まった引数を巻き込まないよう
            // 1件ずつ個別に畳む
            return HookArgRegex.Replace(commandLine, "<hooks 引数を省略>");
        }

        /// <summary>ヒット同士の間が空白だけか（＝ひとかたまりとして畳んでよいか）。</summary>
        private static bool IsOnlyWhitespaceBetween(string commandLine, MatchCollection matches)
        {
            for (var i = 1; i < matches.Count; i++)
            {
                var gapStart = matches[i - 1].Index + matches[i - 1].Length;
                var gapLength = matches[i].Index - gapStart;
                if (gapLength > 0 && !commandLine.AsSpan(gapStart, gapLength).IsWhiteSpace())
                    return false;
            }
            return true;
        }
    }
}
