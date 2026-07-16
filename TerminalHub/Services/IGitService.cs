using TerminalHub.Models;

namespace TerminalHub.Services
{
    public interface IGitService
    {
        /// <summary>
        /// 指定されたパスがGitリポジトリかどうかを判定
        /// </summary>
        /// <param name="path">チェックするフォルダパス</param>
        /// <returns>Gitリポジトリの場合true</returns>
        Task<bool> IsGitRepositoryAsync(string path);

        /// <summary>
        /// 現在のブランチ名を取得
        /// </summary>
        /// <param name="path">リポジトリのパス</param>
        /// <returns>ブランチ名、取得できない場合はnull</returns>
        Task<string?> GetCurrentBranchAsync(string path);

        /// <summary>
        /// Gitリポジトリの詳細情報を取得
        /// </summary>
        /// <param name="path">リポジトリのパス</param>
        /// <returns>Git情報、リポジトリでない場合はnull</returns>
        Task<GitInfo?> GetGitInfoAsync(string path);

        /// <summary>
        /// Worktreeを作成（新規ブランチ／既存ブランチ／detached を切替）
        /// </summary>
        /// <param name="sourcePath">元のリポジトリパス</param>
        /// <param name="branchName">ブランチ名。detach=true の場合は無視可</param>
        /// <param name="worktreePath">Worktreeのパス</param>
        /// <param name="detach">true の場合 --detach で作成（ブランチを作らない）</param>
        /// <returns>成功した場合true</returns>
        Task<bool> CreateWorktreeAsync(string sourcePath, string branchName, string worktreePath, bool detach = false);

        /// <summary>
        /// 指定されたパスがWorktreeかどうかを判定
        /// </summary>
        /// <param name="path">チェックするパス</param>
        /// <returns>Worktreeの場合true</returns>
        Task<bool> IsWorktreeAsync(string path);

        /// <summary>
        /// リポジトリのWorktree一覧を取得
        /// </summary>
        /// <param name="path">リポジトリのパス</param>
        /// <returns>Worktree情報のリスト</returns>
        Task<List<WorktreeInfo>> GetWorktreeListAsync(string path);

        /// <summary>
        /// 未コミットの変更ファイル一覧を取得（git status --porcelain）
        /// </summary>
        /// <param name="path">リポジトリのパス</param>
        /// <returns>変更ファイルのリスト。取得失敗時は null（変更なしの空リストと区別する）</returns>
        Task<List<GitChangedFile>?> GetChangedFilesAsync(string path);

        /// <summary>
        /// コミットハッシュ（短縮形可）からコミット情報を取得
        /// </summary>
        /// <param name="path">リポジトリのパス</param>
        /// <param name="hash">コミットハッシュ（7〜40桁の16進）</param>
        /// <returns>コミット情報。ハッシュがコミットに解決できない場合は null</returns>
        Task<GitCommitInfo?> GetCommitInfoAsync(string path, string hash);

        /// <summary>
        /// origin が GitHub リポジトリの場合、指定番号の PR ページ URL を組み立てて返す。
        /// GitHub の /pull/{n} は対象が Issue なら /issues/{n} へ自動リダイレクトされるため PR/Issue の区別は不要。
        /// </summary>
        /// <param name="path">リポジトリのパス</param>
        /// <param name="number">PR/Issue 番号</param>
        /// <returns>PR ページ URL。origin が github.com でない・取得失敗時は null</returns>
        Task<string?> GetGitHubPrUrlAsync(string path, int number);
    }

    /// <summary>
    /// コミット情報ダイアログ用のコミット詳細（diff 本文は持たない）
    /// </summary>
    public class GitCommitInfo
    {
        public string FullHash { get; set; } = string.Empty;
        public string ShortHash => FullHash.Length > 7 ? FullHash.Substring(0, 7) : FullHash;
        public string Author { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        /// <summary>コミットメッセージ本文（subject 以降）。無ければ空</summary>
        public string Body { get; set; } = string.Empty;
        /// <summary>マージコミット（親が2つ以上）か</summary>
        public bool IsMerge { get; set; }
        public List<GitCommitFileChange> Files { get; set; } = new();
    }

    /// <summary>
    /// git show --numstat の1行に対応する変更ファイル情報
    /// </summary>
    public class GitCommitFileChange
    {
        /// <summary>追加行数。バイナリファイルは null</summary>
        public int? Added { get; set; }
        /// <summary>削除行数。バイナリファイルは null</summary>
        public int? Deleted { get; set; }
        public string FilePath { get; set; } = string.Empty;
    }

    /// <summary>
    /// git status --porcelain の1行に対応する変更ファイル情報
    /// </summary>
    public class GitChangedFile
    {
        /// <summary>ステージ側の状態 (porcelain の X 列。' '=変更なし, M/A/D/R/C/U, ?=未追跡)</summary>
        public char IndexStatus { get; set; }
        /// <summary>作業ツリー側の状態 (porcelain の Y 列)</summary>
        public char WorkTreeStatus { get; set; }
        public string FilePath { get; set; } = string.Empty;
        /// <summary>リネーム/コピー時の元パス</summary>
        public string? OldFilePath { get; set; }

        public bool IsUntracked => IndexStatus == '?' && WorkTreeStatus == '?';
        /// <summary>ステージ済みの変更を含むか</summary>
        public bool IsStaged => !IsUntracked && IndexStatus != ' ';
    }

    public class GitInfo
    {
        public string CurrentBranch { get; set; } = string.Empty;
        public bool HasUncommittedChanges { get; set; }
        public bool IsWorktree { get; set; }
        public List<string> AvailableBranches { get; set; } = new();
    }

    public class WorktreeInfo
    {
        public string Path { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public string? CommitHash { get; set; }
        public bool IsMain { get; set; }
        public bool IsLocked { get; set; }
        public bool IsPrunable { get; set; }
    }
}