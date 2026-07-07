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

        public async Task<bool> CreateWorktreeAsync(string sourcePath, string branchName, string worktreePath, bool detach = false)
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

                string command;
                if (detach)
                {
                    // ブランチを作らず detached HEAD で作成
                    command = $"worktree add --detach \"{worktreePath}\"";
                }
                else
                {
                    // ブランチが既に存在するかチェック
                    var branchCheckResult = await ExecuteGitCommandAsync(sourcePath, $"rev-parse --verify refs/heads/{branchName}");

                    if (branchCheckResult.Success)
                    {
                        // 既存のブランチを使用してWorktreeを作成
                        command = $"worktree add \"{worktreePath}\" \"{branchName}\"";
                    }
                    else
                    {
                        // 新しいブランチでWorktreeを作成
                        command = $"worktree add -b \"{branchName}\" \"{worktreePath}\"";
                    }
                }

                var result = await ExecuteGitCommandAsync(sourcePath, command);

                if (result.Success)
                {
                    _logger.LogInformation("Worktree作成成功: detach={Detach}, ブランチ={Branch}, パス={Path}", detach, branchName, worktreePath);
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

        public async Task<List<WorktreeInfo>> GetWorktreeListAsync(string path)
        {
            var worktrees = new List<WorktreeInfo>();

            try
            {
                if (!await IsGitRepositoryAsync(path))
                    return worktrees;

                var result = await ExecuteGitCommandAsync(path, "worktree list --porcelain");
                if (!result.Success || string.IsNullOrEmpty(result.Output))
                    return worktrees;

                var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                WorktreeInfo? currentWorktree = null;

                foreach (var line in lines)
                {
                    if (line.StartsWith("worktree "))
                    {
                        if (currentWorktree != null)
                            worktrees.Add(currentWorktree);

                        currentWorktree = new WorktreeInfo
                        {
                            Path = line.Substring("worktree ".Length).Trim()
                        };
                    }
                    else if (currentWorktree != null)
                    {
                        if (line.StartsWith("HEAD "))
                        {
                            currentWorktree.CommitHash = line.Substring("HEAD ".Length).Trim();
                        }
                        else if (line.StartsWith("branch "))
                        {
                            currentWorktree.BranchName = line.Substring("branch ".Length).Trim();
                        }
                        else if (line == "bare")
                        {
                            // bareリポジトリの場合は無視
                            currentWorktree = null;
                        }
                        else if (line == "detached")
                        {
                            currentWorktree.BranchName = "(detached HEAD)";
                        }
                        else if (line == "locked")
                        {
                            currentWorktree.IsLocked = true;
                        }
                        else if (line == "prunable")
                        {
                            currentWorktree.IsPrunable = true;
                        }
                    }
                }

                if (currentWorktree != null)
                    worktrees.Add(currentWorktree);

                // 最初のWorktreeがメインリポジトリ
                if (worktrees.Count > 0)
                    worktrees[0].IsMain = true;

                return worktrees;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worktree一覧取得でエラーが発生しました: {Path}", path);
                return worktrees;
            }
        }

        public async Task<WorktreeInfo?> ValidateWorktreeAsync(string worktreePath)
        {
            try
            {
                if (!Directory.Exists(worktreePath))
                    return null;

                // Worktreeかどうかチェック
                if (!await IsWorktreeAsync(worktreePath))
                    return null;

                // git worktree listでこのWorktreeの情報を取得
                var result = await ExecuteGitCommandAsync(worktreePath, "worktree list --porcelain");
                if (!result.Success || string.IsNullOrEmpty(result.Output))
                    return null;

                var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                WorktreeInfo? targetWorktree = null;
                WorktreeInfo? currentWorktree = null;

                foreach (var line in lines)
                {
                    if (line.StartsWith("worktree "))
                    {
                        var path = line.Substring("worktree ".Length).Trim();
                        currentWorktree = new WorktreeInfo { Path = path };

                        // パスが一致する場合、これが対象のWorktree
                        if (Path.GetFullPath(path).Equals(Path.GetFullPath(worktreePath), StringComparison.OrdinalIgnoreCase))
                        {
                            targetWorktree = currentWorktree;
                        }
                    }
                    else if (currentWorktree != null && currentWorktree == targetWorktree)
                    {
                        if (line.StartsWith("HEAD "))
                        {
                            currentWorktree.CommitHash = line.Substring("HEAD ".Length).Trim();
                        }
                        else if (line.StartsWith("branch "))
                        {
                            currentWorktree.BranchName = line.Substring("branch ".Length).Trim();
                        }
                        else if (line == "detached")
                        {
                            currentWorktree.BranchName = "(detached HEAD)";
                        }
                        else if (line == "locked")
                        {
                            currentWorktree.IsLocked = true;
                        }
                        else if (line == "prunable")
                        {
                            currentWorktree.IsPrunable = true;
                        }
                    }
                }

                return targetWorktree;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worktree検証でエラーが発生しました: {Path}", worktreePath);
                return null;
            }
        }

        public async Task<List<GitChangedFile>?> GetChangedFilesAsync(string path)
        {
            try
            {
                if (!await IsGitRepositoryAsync(path))
                    return null;

                // core.quotepath=false: 日本語等の非ASCIIパスが \346... 形式にエスケープされるのを防ぐ
                var result = await ExecuteGitCommandAsync(path, "-c core.quotepath=false status --porcelain");
                if (!result.Success)
                    return null;

                var files = new List<GitChangedFile>();
                foreach (var line in (result.Output ?? "").Split('\n'))
                {
                    // porcelain v1 形式: "XY パス" (X=ステージ側, Y=作業ツリー側)
                    var trimmed = line.TrimEnd('\r');
                    if (trimmed.Length < 4)
                        continue;

                    var entry = new GitChangedFile
                    {
                        IndexStatus = trimmed[0],
                        WorkTreeStatus = trimmed[1],
                    };

                    var pathPart = trimmed.Substring(3);
                    // リネーム/コピーは "元パス -> 新パス" 形式
                    var arrowIndex = pathPart.IndexOf(" -> ", StringComparison.Ordinal);
                    if (arrowIndex >= 0 && (entry.IndexStatus is 'R' or 'C'))
                    {
                        entry.OldFilePath = Unquote(pathPart.Substring(0, arrowIndex));
                        entry.FilePath = Unquote(pathPart.Substring(arrowIndex + 4));
                    }
                    else
                    {
                        entry.FilePath = Unquote(pathPart);
                    }

                    files.Add(entry);
                }

                return files;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "変更ファイル一覧の取得でエラーが発生しました: {Path}", path);
                return null;
            }

            // スペース等を含むパスは "..." で囲まれるため外す
            static string Unquote(string s)
                => s.Length >= 2 && s.StartsWith('"') && s.EndsWith('"')
                    ? s.Substring(1, s.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\")
                    : s;
        }

        public async Task<GitCommitInfo?> GetCommitInfoAsync(string path, string hash)
        {
            try
            {
                // 正規表現由来の入力だが、コマンド組み立てに使うため念のため形式を検証する
                if (string.IsNullOrEmpty(hash) || !System.Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9a-fA-F]{7,40}$"))
                    return null;

                if (!await IsGitRepositoryAsync(path))
                    return null;

                // コミットに解決できるか検証しつつフルハッシュを得る（blob/tree 等は対象外）
                var resolve = await ExecuteGitCommandAsync(path, $"rev-parse --verify --quiet {hash}^{{commit}}");
                var fullHash = resolve.Output?.Trim();
                if (!resolve.Success || string.IsNullOrEmpty(fullHash))
                    return null;

                // メタ情報: 1〜4行目 = フルハッシュ/親/作者+日時/件名、5行目以降 = 本文
                var meta = await ExecuteGitCommandAsync(path,
                    $"show -s --date=format:\"%Y-%m-%d %H:%M\" --format=\"%H%n%P%n%an / %ad%n%s%n%b\" {fullHash}");
                if (!meta.Success || string.IsNullOrEmpty(meta.Output))
                    return null;

                var lines = meta.Output.Replace("\r", "").Split('\n');
                if (lines.Length < 4)
                    return null;

                var authorDate = lines[2].Split(" / ", 2);
                var info = new GitCommitInfo
                {
                    FullHash = lines[0],
                    IsMerge = lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2,
                    Author = authorDate.Length == 2 ? authorDate[0] : lines[2],
                    Date = authorDate.Length == 2 ? authorDate[1] : "",
                    Subject = lines[3],
                    Body = string.Join('\n', lines.Skip(4)).Trim(),
                };

                // 変更ファイル一覧（追加/削除行数付き）。マージコミットは numstat が空になるが、そのまま扱う
                var stat = await ExecuteGitCommandAsync(path, $"-c core.quotepath=false show --numstat --format= {fullHash}");
                if (stat.Success && !string.IsNullOrEmpty(stat.Output))
                {
                    foreach (var line in stat.Output.Replace("\r", "").Split('\n'))
                    {
                        // numstat 形式: "追加\t削除\tパス"（バイナリは "-\t-\tパス"）
                        var parts = line.Split('\t', 3);
                        if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[2]))
                            continue;
                        info.Files.Add(new GitCommitFileChange
                        {
                            Added = int.TryParse(parts[0], out var a) ? a : null,
                            Deleted = int.TryParse(parts[1], out var d) ? d : null,
                            FilePath = parts[2],
                        });
                    }
                }

                return info;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "コミット情報の取得でエラーが発生しました: {Path} {Hash}", path, hash);
                return null;
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

                // タイムアウト付きで待機（10秒）
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch { }
                    _logger.LogWarning("Gitコマンドがタイムアウトしました: git {Arguments} in {WorkingDirectory}", arguments, workingDirectory);
                    return (false, null, "タイムアウト");
                }

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