using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using TerminalHub.Constants;

namespace TerminalHub.Services
{
    /// <summary>
    /// テキスト事前整形（実験的機能）。
    /// 専用ワークディレクトリ (%LOCALAPPDATA%\TerminalHub\refine-workdir\) を用意し、
    /// そこに整形指示用の CLAUDE.md を配置した上で Claude Code CLI を -p モードで呼び出す。
    /// これによりユーザーの入力テキストだけを渡せば、CLAUDE.md が自動で指示として
    /// 適用されるため、プロンプトエスケープやテンプレート管理が不要になる。
    /// </summary>
    public interface ITextRefineService
    {
        /// <summary>
        /// テキストを Claude Code CLI 経由で整形する。
        /// 失敗時は null を返す（エラーは Logger に出力）。
        /// </summary>
        Task<string?> RefineAsync(string text, CancellationToken cancellationToken = default);
    }

    public class TextRefineService : ITextRefineService
    {
        private readonly ILogger<TextRefineService> _logger;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private bool _initialized = false;
        private string _workdir = "";

        public TextRefineService(ILogger<TextRefineService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// ワークディレクトリを初期化して CLAUDE.md を配置する。
        /// 既に CLAUDE.md が存在する場合は上書きしない（ユーザーのカスタマイズを尊重）。
        /// </summary>
        private async Task EnsureInitializedAsync()
        {
            if (_initialized) return;
            await _initLock.WaitAsync();
            try
            {
                if (_initialized) return;

                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                _workdir = Path.Combine(appData, "TerminalHub", "refine-workdir");
                Directory.CreateDirectory(_workdir);

                var claudeMdPath = Path.Combine(_workdir, "CLAUDE.md");
                if (!File.Exists(claudeMdPath))
                {
                    await File.WriteAllTextAsync(claudeMdPath, DefaultClaudeMd);
                    _logger.LogInformation("[TextRefine] CLAUDE.md を生成: {Path}", claudeMdPath);
                }

                _initialized = true;
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task<string?> RefineAsync(string text, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            try
            {
                await EnsureInitializedAsync();

                var (installed, exePath, _) = TerminalConstants.GetClaudeCodeInstallInfo();
                if (!installed)
                {
                    _logger.LogWarning("[TextRefine] Claude Code CLI が見つかりません: {Path}", exePath);
                    return null;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = _workdir,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                // -p: print mode (非対話、stdout に結果を出して終了)
                // --model: 軽量モデル指定
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(text);
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add("claude-haiku-4-5");

                using var process = new Process { StartInfo = psi };
                if (!process.Start())
                {
                    _logger.LogWarning("[TextRefine] プロセス起動に失敗");
                    return null;
                }

                // stdout / stderr を並行読み取り（デッドロック回避）
                var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

                // タイムアウト保険 (30秒)
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    _logger.LogWarning("[TextRefine] タイムアウトで強制終了");
                    return null;
                }

                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                if (process.ExitCode != 0)
                {
                    _logger.LogWarning("[TextRefine] 非 0 終了コード: {Code} / stderr: {Err}",
                        process.ExitCode, stderr);
                    return null;
                }

                var result = stdout?.TrimEnd('\r', '\n');
                return string.IsNullOrEmpty(result) ? null : result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TextRefine] 整形中にエラー");
                return null;
            }
        }

        private const string DefaultClaudeMd = @"# テキスト整形アシスタント

あなたは日本語テキストを整える専門家です。TerminalHub というツールから事前整形用途で呼び出されています。

## 動作ルール

- 入力されたテキストの**誤字脱字・表現の揺れ**のみを自然に修正してください。
- **内容や意図は一切変えない**でください。
- 解説・挨拶・補足・前置きは付けず、**整形後のテキストのみ**を出力してください。
- ファイル操作やツール使用はしないでください（純粋なテキスト変換のみ）。
- 入力がすでに正しい場合はそのまま返してください。
- 出力の末尾に余計な改行や空行を入れないでください。

## 例

入力:
```
これはてすとです、うまく動くかな
```

出力:
```
これはテストです。うまく動くかな？
```

---

※ このファイルは TerminalHub の初回起動時に自動生成されたものです。
自由に編集して好みの整形ルールにカスタマイズできます。削除すると次回起動時に再生成されます。
";
    }
}
