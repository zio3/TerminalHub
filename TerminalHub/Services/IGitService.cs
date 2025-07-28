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
        /// 新しいブランチを作成してWorktreeを作成
        /// </summary>
        /// <param name="sourcePath">元のリポジトリパス</param>
        /// <param name="branchName">新しいブランチ名</param>
        /// <param name="worktreePath">Worktreeのパス</param>
        /// <returns>成功した場合true</returns>
        Task<bool> CreateWorktreeAsync(string sourcePath, string branchName, string worktreePath);

        /// <summary>
        /// Worktreeを削除
        /// </summary>
        /// <param name="worktreePath">削除するWorktreeのパス</param>
        /// <returns>成功した場合true</returns>
        Task<bool> RemoveWorktreeAsync(string worktreePath);

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
        /// 既存のWorktreeを検証
        /// </summary>
        /// <param name="worktreePath">検証するWorktreeのパス</param>
        /// <returns>Worktree情報、無効な場合はnull</returns>
        Task<WorktreeInfo?> ValidateWorktreeAsync(string worktreePath);
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