namespace TerminalHub.Helpers
{
    /// <summary>
    /// worktree フォルダの表示名を決める。
    ///
    /// TerminalHub が作る worktree は親と同じ階層に「{親フォルダ名}-{サフィックス}」で置かれる
    /// （SessionManager.CreateWorktreeSessionAsync）。一覧では親の下にぶら下げて描画するので、
    /// 行に親の名前を再掲しても幅を食うだけになる。ここで親のプレフィックスを落として
    /// サフィックスだけ（worktree-1 等）にする。
    ///
    /// 命名規則に沿わないフォルダ（ユーザーが自分で作った worktree など）はフォルダ名をそのまま使う。
    /// </summary>
    public static class WorktreeNaming
    {
        public static string GetShortName(string? parentFolderPath, string worktreePath)
        {
            if (string.IsNullOrWhiteSpace(worktreePath))
                return string.Empty;

            var folderName = Path.GetFileName(TrimSeparators(worktreePath));
            if (string.IsNullOrEmpty(folderName))
                return worktreePath;

            if (string.IsNullOrWhiteSpace(parentFolderPath))
                return folderName;

            var parentName = Path.GetFileName(TrimSeparators(parentFolderPath));
            if (string.IsNullOrEmpty(parentName))
                return folderName;

            // 「親フォルダ名-」で始まるときだけ削る。削った結果が空になる場合は削らない
            var prefix = parentName + "-";
            if (folderName.Length > prefix.Length &&
                folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return folderName.Substring(prefix.Length);
            }

            return folderName;
        }

        private static string TrimSeparators(string path) =>
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
