using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TerminalHub.Models;

namespace TerminalHub.Services;

/// <summary>
/// スラッシュコマンド補完の候補ソース。
///
/// Claude Code は列挙専用フラグを持たないが、headless モード
/// （<c>claude -p ... --output-format stream-json --verbose</c>）の最初の system init 行に
/// <c>slash_commands</c>（名前のみの配列）が入る。これは API リクエストの前に出力されるため、
/// init を読んだ瞬間にプロセスを落とせばトークン消費なしで「インストール済みバージョンの
/// 実際のコマンド一覧」を取得できる（組み込み＋バンドルスキル＋プラグイン＋自作コマンドを含む）。
///
/// 説明文は init には含まれないので、<see cref="SlashCommandCatalog"/> の手元辞書を
/// 名前一致で上書きして補う（知らない名前は名前だけ表示）。
///
/// 取得は重い（別プロセス起動 ~1秒）ので、プロセス全体で1回だけ実行してキャッシュする。
/// 取得できるまで／失敗時は <see cref="SlashCommandCatalog"/> の静的辞書へフォールバックする。
/// </summary>
public class SlashCommandProvider
{
    private readonly ILogger<SlashCommandProvider> _logger;
    private readonly object _lock = new();

    // TerminalType 単位のキャッシュ（現状 ClaudeCode のみ動的取得する）。
    private readonly Dictionary<TerminalType, IReadOnlyList<SlashCommandItem>> _cache = new();
    // 種別ごとの読み込みタスク（多重起動防止）。
    private readonly Dictionary<TerminalType, Task> _loading = new();

    /// <summary>動的取得が完了してキャッシュが更新されたときに発火（UI 再描画のトリガ用）。</summary>
    public event Action? Changed;

    public SlashCommandProvider(ILogger<SlashCommandProvider> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 現時点の候補を同期で返す。動的取得済みならそれを、未取得／非対応なら静的辞書を返す。
    /// </summary>
    public IReadOnlyList<SlashCommandItem> GetCommands(TerminalType type)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(type, out var cached))
                return cached;
        }
        return SlashCommandCatalog.ForTerminalType(type);
    }

    /// <summary>
    /// 動的取得をバックグラウンドで一度だけ起動する（対応種別のみ）。
    /// 既に取得済み／取得中なら何もしない。
    ///
    /// 取得は「実プロジェクトを汚さない」ため専用の一時フォルダを cwd にして実行する
    /// （実プロジェクトの /resume 履歴や project固有フックを触らない）。この副作用として
    /// 一時フォルダ固有の transcript が生成されるが、取得後に best-effort で削除する。
    /// v1 は組み込み中心なので、プロジェクト固有の自作コマンドは拾わない割り切り。
    /// </summary>
    public void EnsureLoaded(TerminalType type)
    {
        // 現状 Claude Code のみ init から一覧が取れる。
        if (type != TerminalType.ClaudeCode) return;

        lock (_lock)
        {
            if (_cache.ContainsKey(type)) return;
            if (_loading.ContainsKey(type)) return;
            _loading[type] = Task.Run(() => LoadAsync(type));
        }
    }

    private async Task LoadAsync(TerminalType type)
    {
        try
        {
            var names = await FetchSlashCommandNamesAsync();
            if (names is { Count: > 0 })
            {
                var merged = MergeWithDescriptions(type, names);
                lock (_lock)
                {
                    _cache[type] = merged;
                }
                _logger.LogDebug("[SlashEnum] {Type}: 動的取得 {Count} 件", type, merged.Count);
                Changed?.Invoke();
            }
            else
            {
                _logger.LogDebug("[SlashEnum] {Type}: 動的取得は空。静的辞書へフォールバック", type);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SlashEnum] {Type}: 動的取得に失敗。静的辞書へフォールバック", type);
        }
        finally
        {
            lock (_lock)
            {
                _loading.Remove(type);
            }
        }
    }

    /// <summary>
    /// init 行に出る名前（"/" 無し）に、手元辞書の説明を名前一致で付与する。
    /// 説明が無いものは名前だけ（Description=null）で並べる。表示名は先頭に "/" を付ける。
    /// </summary>
    private static IReadOnlyList<SlashCommandItem> MergeWithDescriptions(TerminalType type, IReadOnlyList<string> names)
    {
        var descByName = SlashCommandCatalog.BuildDescriptionMap(type);
        var items = new List<SlashCommandItem>(names.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in names)
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            var display = name.StartsWith("/") ? name : "/" + name;
            if (!seen.Add(display)) continue; // 重複除去
            descByName.TryGetValue(name.TrimStart('/'), out var desc);
            items.Add(new SlashCommandItem(display, desc));
        }
        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return items;
    }

    /// <summary>
    /// headless の init 行から slash_commands（名前配列）を取得する。init を読み次第プロセスを落とす。
    /// 実行は専用の一時フォルダを cwd にして実プロジェクトを汚さない。生成された transcript は後片付けする。
    /// 失敗時（claude が見つからない／タイムアウト等）は null を返す。
    /// </summary>
    private async Task<IReadOnlyList<string>?> FetchSlashCommandNamesAsync()
    {
        var exe = ResolveClaudeExecutable();
        if (exe is null)
        {
            _logger.LogDebug("[SlashEnum] claude 実行ファイルが PATH に見つからない");
            return null;
        }

        var workingDirectory = GetScratchDirectory();

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory,
        };
        // init は API リクエスト前に出るので、ダミープロンプトは実際には送信されない前提。
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("hi");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");
        psi.ArgumentList.Add("--max-turns");
        psi.ArgumentList.Add("1");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var proc = new Process { StartInfo = psi };

        if (!proc.Start())
            return null;

        string? sessionId = null;
        try
        {
            // stdin を即閉じ（対話入力待ちで固まらないように）。
            try { proc.StandardInput.Close(); } catch { }

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(cts.Token)) != null)
            {
                if (line.IndexOf("\"subtype\":\"init\"", StringComparison.Ordinal) < 0)
                    continue;

                var (names, sid) = TryParseInit(line);
                sessionId = sid;
                // init を掴んだら用済み。API リクエスト前にここで打ち切る＝トークン消費なし。
                KillQuietly(proc);
                return names;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[SlashEnum] 取得タイムアウト");
        }
        finally
        {
            KillQuietly(proc);
            // 一時フォルダで生成された transcript を後片付け（best-effort）。
            CleanupTranscript(workingDirectory, sessionId);
        }
        return null;
    }

    private static (IReadOnlyList<string>? Names, string? SessionId) TryParseInit(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;

            string? sessionId = null;
            if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                sessionId = sid.GetString();

            if (!root.TryGetProperty("slash_commands", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return (null, sessionId);

            var list = new List<string>(arr.GetArrayLength());
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
                }
            }
            return (list, sessionId);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>列挙専用の一時作業フォルダ（存在しなければ作る）。</summary>
    private static string GetScratchDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TerminalHub", "slash-enum");
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    /// <summary>
    /// 一時フォルダで生成された Claude Code の transcript(jsonl)を削除する（best-effort）。
    /// 履歴は ~/.claude/projects/&lt;slugged-cwd&gt;/&lt;session_id&gt;.jsonl に落ちる。
    /// slug は cwd のパス区切り/コロンを "-" に置換したもの（例: "C:\a\b" → "C--a-b"）。
    /// </summary>
    private void CleanupTranscript(string workingDirectory, string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return;

            var slug = SlugifyPath(workingDirectory);
            var path = Path.Combine(home, ".claude", "projects", slug, sessionId + ".jsonl");
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogDebug("[SlashEnum] 一時transcriptを削除: {Path}", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SlashEnum] transcript後片付けに失敗（無害）");
        }
    }

    /// <summary>Claude Code の project ディレクトリ名の規則に合わせてパスをスラッグ化する。</summary>
    private static string SlugifyPath(string path)
    {
        var sb = new StringBuilder(path.Length);
        foreach (var ch in path)
            sb.Append(ch is '\\' or '/' or ':' ? '-' : ch);
        return sb.ToString();
    }

    private static void KillQuietly(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch { /* 既に終了 or 権限等。無視 */ }
    }

    /// <summary>
    /// PATH から claude 実行ファイルを解決する（Windows は .exe/.cmd/.bat も試す）。
    /// </summary>
    private static string? ResolveClaudeExecutable()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        var isWindows = OperatingSystem.IsWindows();
        var candidates = isWindows
            ? new[] { "claude.exe", "claude.cmd", "claude.bat", "claude" }
            : new[] { "claude" };

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var name in candidates)
            {
                try
                {
                    var full = Path.Combine(dir, name);
                    if (File.Exists(full)) return full;
                }
                catch { /* 不正な PATH 要素は無視 */ }
            }
        }
        return null;
    }
}
