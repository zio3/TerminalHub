using System.Text.RegularExpressions;

namespace TerminalHub.Services
{
    /// <summary>
    /// ターミナル出力中のファイル/フォルダ表記を、セッションの作業フォルダ基準で実在するフルパスに解決する。
    /// ターミナル内パスのクリック・右クリックメニュー（Root.razor）と Markdown ビューアの API（Program.cs）が
    /// 同じ規則で解決するためにここへ一本化している。規則:
    /// <list type="bullet">
    /// <item><c>~</c> / <c>~/</c> / <c>~\</c> 始まりはユーザープロファイルに展開する</item>
    /// <item>絶対パスはそのまま、相対パスは作業フォルダと結合して正規化する</item>
    /// <item>実在（ファイルまたはフォルダ）しなければ、<c>Root.razor:123</c> のような末尾の行番号表記を落として再試行する</item>
    /// </list>
    /// 不正なパス文字などで解決できないものは null（誤検出クリックの可能性が高いので例外にしない）。
    /// </summary>
    public static class TerminalPathResolver
    {
        private static readonly Regex TrailingLineNumber = new(@"(?::\d+)+$", RegexOptions.Compiled);

        /// <summary>
        /// 先頭の <c>~</c> をユーザープロファイルに展開する（<c>~user</c> のような形は対象外）。
        /// </summary>
        public static string ExpandHomePath(string path)
        {
            if (path.Length == 0 || path[0] != '~')
                return path;
            if (path.Length > 1 && path[1] != '\\' && path[1] != '/')
                return path;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                return path;

            return path.Length == 1 ? home : home.TrimEnd('\\', '/') + path.Substring(1);
        }

        /// <summary>
        /// 実在すればフルパスを返す。見つからなければ null。
        /// </summary>
        public static string? Resolve(string? basePath, string? rawPath)
        {
            if (string.IsNullOrEmpty(basePath) || string.IsNullOrWhiteSpace(rawPath))
                return null;

            var trimmed = rawPath.Trim();
            var resolved = ResolveCandidate(basePath, trimmed);
            if (resolved == null)
            {
                var withoutLineNumber = TrailingLineNumber.Replace(trimmed, "");
                if (withoutLineNumber != trimmed)
                {
                    resolved = ResolveCandidate(basePath, withoutLineNumber);
                }
            }
            return resolved;
        }

        private static string? ResolveCandidate(string basePath, string rawCandidate)
        {
            var candidate = ExpandHomePath(rawCandidate);
            string fullPath;
            try
            {
                fullPath = Path.IsPathRooted(candidate)
                    ? Path.GetFullPath(candidate)
                    : Path.GetFullPath(Path.Combine(basePath, candidate));
            }
            catch
            {
                return null;
            }

            return Directory.Exists(fullPath) || File.Exists(fullPath) ? fullPath : null;
        }
    }
}
