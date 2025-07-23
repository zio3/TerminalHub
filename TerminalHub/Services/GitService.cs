using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace TerminalHub.Services
{
    public class GitService : IGitService
    {
        private readonly ILogger<GitService> _logger;

        public GitService(ILogger<GitService> logger)
        {
            _logger = logger;
        }

        public async Task<bool> IsGitRepositoryAsync(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    return false;

                // まず.gitフォルダの存在をチェック（高速）
                var gitPath = Path.Combine(path, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath)) // .gitファイルの場合もある（worktree）
                {
                    return true;
                }

                // .gitフォルダが見つからない場合、git rev-parseで確認
                var result = await ExecuteGitCommandAsync(path, "rev-parse --git-dir");
                return result.Success;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gitリポジトリ検出でエラーが発生しました: {Path}", path);
                return false;
            }
        }

        public async Task<string?> GetCurrentBranchAsync(string path)
        {
            try
            {
                var result = await ExecuteGitCommandAsync(path, "branch --show-current");
                return result.Success ? result.Output?.Trim() : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "現在のブランチ取得でエラーが発生しました: {Path}", path);
                return null;
            }
        }

        public async Task<GitInfo?> GetGitInfoAsync(string path)
        {
            try
            {
                if (!await IsGitRepositoryAsync(path))
                    return null;

                var gitInfo = new GitInfo();

                // 現在のブランチを取得
                gitInfo.CurrentBranch = await GetCurrentBranchAsync(path) ?? "unknown";

                // 未コミットの変更があるかチェック
                var statusResult = await ExecuteGitCommandAsync(path, "status --porcelain");
                gitInfo.HasUncommittedChanges = statusResult.Success && !string.IsNullOrWhiteSpace(statusResult.Output);

                // Worktreeかどうかをチェック
                gitInfo.IsWorktree = await IsWorktreeAsync(path);

                // Worktreeの場合、メインリポジトリのパスを取得
                if (gitInfo.IsWorktree)
                {
                    var worktreeListResult = await ExecuteGitCommandAsync(path, "worktree list --porcelain");
                    if (worktreeListResult.Success)
                    {
                        var lines = worktreeListResult.Output?.Split('\n') ?? Array.Empty<string>();
                        var mainWorktree = lines.FirstOrDefault(l => l.StartsWith("worktree ") && !l.Contains("detached"));
                        if (mainWorktree != null)
                        {
                            gitInfo.WorktreeMainPath = mainWorktree.Substring("worktree ".Length).Trim();
                        }
                    }
                }

                // 利用可能なブランチを取得
                var branchResult = await ExecuteGitCommandAsync(path, "branch -a --format='%(refname:short)'");
                if (branchResult.Success && !string.IsNullOrEmpty(branchResult.Output))
                {
                    gitInfo.AvailableBranches = branchResult.Output
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(b => b.Trim().Trim('\''))
                        .Where(b => !string.IsNullOrEmpty(b))
                        .ToList();
                }

                return gitInfo;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Git情報取得でエラーが発生しました: {Path}", path);
                return null;
            }
        }

        public async Task<bool> CreateWorktreeAsync(string sourcePath, string branchName, string worktreePath)
        {
            try
            {
                if (!await IsGitRepositoryAsync(sourcePath))
                {
                    _logger.LogWarning("Worktree作成: ソースパスがGitリポジトリではありません: {SourcePath}", sourcePath);
                    return false;
                }

                if (Directory.Exists(worktreePath))
                {
                    _logger.LogWarning("Worktree作成: 対象パスが既に存在します: {WorktreePath}", worktreePath);
                    return false;
                }

                // 新しいブランチでWorktreeを作成
                var command = $"worktree add -b \"{branchName}\" \"{worktreePath}\"";
                var result = await ExecuteGitCommandAsync(sourcePath, command);

                if (result.Success)
                {
                    _logger.LogInformation("Worktree作成成功: ブランチ={Branch}, パス={Path}", branchName, worktreePath);
                    return true;
                }
                else
                {
                    _logger.LogWarning("Worktree作成失敗: {Error}", result.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worktree作成でエラーが発生しました: ブランチ={Branch}, パス={Path}", branchName, worktreePath);
                return false;
            }
        }

        public async Task<bool> RemoveWorktreeAsync(string worktreePath)
        {
            try
            {
                if (!await IsWorktreeAsync(worktreePath))
                {
                    _logger.LogWarning("Worktree削除: 指定されたパスはWorktreeではありません: {Path}", worktreePath);
                    return false;
                }

                var command = $"worktree remove \"{worktreePath}\"";
                var result = await ExecuteGitCommandAsync(worktreePath, command);

                if (result.Success)
                {
                    _logger.LogInformation("Worktree削除成功: {Path}", worktreePath);
                    return true;
                }
                else
                {
                    _logger.LogWarning("Worktree削除失敗: {Error}", result.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worktree削除でエラーが発生しました: {Path}", worktreePath);
                return false;
            }
        }

        public async Task<bool> IsWorktreeAsync(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    return false;

                // .gitファイルが存在するかチェック（Worktreeの場合は.gitはファイル）
                var gitPath = Path.Combine(path, ".git");
                if (File.Exists(gitPath))
                {
                    // .gitファイルの内容を確認
                    var gitContent = await File.ReadAllTextAsync(gitPath);
                    return gitContent.StartsWith("gitdir:");
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Worktree判定でエラーが発生しました: {Path}", path);
                return false;
            }
        }

        private async Task<(bool Success, string? Output, string? Error)> ExecuteGitCommandAsync(string workingDirectory, string arguments)
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                        outputBuilder.AppendLine(e.Data);
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                        errorBuilder.AppendLine(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                var output = outputBuilder.ToString();
                var error = errorBuilder.ToString();

                return (process.ExitCode == 0, output, error);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gitコマンド実行でエラーが発生しました: {Command}", arguments);
                return (false, null, ex.Message);
            }
        }
    }
}