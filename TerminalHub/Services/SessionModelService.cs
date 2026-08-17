using System.Text;
using System.Text.RegularExpressions;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    /// <summary>
    /// CLI が残すトランスクリプト（.jsonl）から実使用モデルを読み取る。
    ///
    /// セッション本人（システムプロンプト）に聞いてはいけない: そこに出るモデル名は起動時の値で、
    /// 途中の /model 切り替えが反映されない。assistant の発話に記録された model だけが実測値。
    /// </summary>
    public class SessionModelService : ISessionModelService
    {
        private readonly ILogger<SessionModelService> _logger;

        // 直近の取得結果。短時間に何度も呼ばれても実ファイルを読み直さないための緩衝。
        private readonly Dictionary<Guid, (string? Model, DateTime ReadAt)> _cache = new();
        private readonly SemaphoreSlim _lock = new(1, 1);

        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
        /// <summary>Codex の rollout 一覧（cwd 付き）を作り直す間隔</summary>
        private static readonly TimeSpan IndexTtl = TimeSpan.FromMinutes(2);
        /// <summary>Codex の走査上限。全件でも数百程度だが、増え続ける前提で頭を抑える</summary>
        private const int CodexScanLimit = 500;
        /// <summary>末尾から読むバイト数。巨大な jsonl 全体を読むと極端に遅くなる</summary>
        private const int TailBytes = 256 * 1024;

        private static readonly Regex ModelRegex = new("\"model\":\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex CwdRegex = new("\"cwd\":\"([^\"]+)\"", RegexOptions.Compiled);

        public SessionModelService(ILogger<SessionModelService> logger)
        {
            _logger = logger;
        }

        public async Task<string?> GetCurrentModelAsync(SessionInfo session)
        {
            if (string.IsNullOrEmpty(session.FolderPath)) return null;

            // トランスクリプトを持つのは Claude Code と Codex のみ。他種別は対象外
            if (session.TerminalType != TerminalType.ClaudeCode && session.TerminalType != TerminalType.CodexCLI)
                return null;

            await _lock.WaitAsync();
            try
            {
                if (_cache.TryGetValue(session.SessionId, out var cached)
                    && DateTime.UtcNow - cached.ReadAt < CacheTtl)
                {
                    return cached.Model;
                }

                var model = await Task.Run(() => session.TerminalType == TerminalType.ClaudeCode
                    ? ReadClaudeModel(session.FolderPath)
                    : ReadCodexModel(session.FolderPath));

                _cache[session.SessionId] = (model, DateTime.UtcNow);
                return model;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SessionModelService] モデル取得に失敗: {Path}", session.FolderPath);
                return null;
            }
            finally
            {
                _lock.Release();
            }
        }

        // --- Claude Code -----------------------------------------------------
        // ~/.claude/projects/<記号を - に潰した cwd>/<uuid>.jsonl

        private string? ReadClaudeModel(string folderPath)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (!Directory.Exists(root)) return null;

            var key = ToProjectKey(folderPath);
            if (key == null) return null;

            // ディレクトリ名は既にエンコード済みだが、大文字小文字が揃わないので同じ変換をかけて突き合わせる
            var dir = Directory.EnumerateDirectories(root)
                .FirstOrDefault(d => string.Equals(ToProjectKey(Path.GetFileName(d)), key, StringComparison.Ordinal));
            if (dir == null) return null;

            // 更新日時での足切りはしない。長く放置したセッションでもモデルは変わらず有効なため
            // （フォルダが一致した時点で対象は確定しているので、その中の最新ファイルを見れば足りる）
            var jsonl = Directory.EnumerateFiles(dir, "*.jsonl")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault()?.FullName;
            if (jsonl == null) return null;

            // 末尾から遡り、最後に assistant が喋ったときのモデルを拾う
            foreach (var line in ReadTailLines(jsonl).Reverse())
            {
                if (!line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal)) continue;
                var m = ModelRegex.Match(line);
                if (m.Success && IsRealModel(m.Groups[1].Value)) return Shorten(m.Groups[1].Value);
            }
            return null;
        }

        /// <summary>パス -> projects のフォルダ名。末尾の空白や区切りは落としてから記号を潰す</summary>
        private static string? ToProjectKey(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var p = path.Trim().TrimEnd('\\', '/', ' ');
            var sb = new StringBuilder(p.Length);
            foreach (var c in p)
                sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
            return sb.ToString().TrimEnd('-').ToLowerInvariant();
        }

        // --- Codex -----------------------------------------------------------
        // ~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl（先頭行の session_meta に cwd が生パスで入る）

        private string? ReadCodexModel(string folderPath)
        {
            var target = folderPath.Trim().TrimEnd('\\', '/', ' ');

            // 一覧作りはセッション毎にやると同じ走査を人数分繰り返すので、まとめて作って使い回す
            foreach (var (path, cwd) in GetCodexIndex())
            {
                if (!string.Equals(cwd, target, StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var line in ReadTailLines(path).Reverse())
                {
                    var m = ModelRegex.Match(line);
                    if (m.Success && IsRealModel(m.Groups[1].Value)) return Shorten(m.Groups[1].Value);
                }
                return null;
            }
            return null;
        }

        private List<(string Path, string Cwd)>? _codexIndex;
        private DateTime _codexIndexAt;

        /// <summary>rollout を新しい順に並べ、先頭行から cwd を取った一覧。サブエージェントは除く</summary>
        private List<(string Path, string Cwd)> GetCodexIndex()
        {
            if (_codexIndex != null && DateTime.UtcNow - _codexIndexAt < IndexTtl) return _codexIndex;

            var index = new List<(string, string)>();
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
            if (!Directory.Exists(root))
            {
                _codexIndex = index;
                _codexIndexAt = DateTime.UtcNow;
                return index;
            }

            var files = Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(CodexScanLimit);

            foreach (var file in files)
            {
                var head = ReadHead(file.FullName);
                if (head == null) continue;

                // サブエージェントの記録には、モデル名ではない値（審査エージェント名等）が入るため除外する
                if (head.Contains("\"thread_source\":\"subagent\"", StringComparison.Ordinal)) continue;

                var cwd = CwdRegex.Match(head);
                if (!cwd.Success) continue;

                index.Add((file.FullName, cwd.Groups[1].Value.Replace("\\\\", "\\").Trim().TrimEnd('\\', '/', ' ')));
            }

            _codexIndex = index;
            _codexIndexAt = DateTime.UtcNow;
            return index;
        }

        // --- 共通 -------------------------------------------------------------

        /// <summary>
        /// 実モデル名か。Claude Code は API エラー時の代替応答等に "&lt;synthetic&gt;" を記録するので、
        /// これをバッジに出さないよう弾き、さらに遡って本物のモデルを探す
        /// </summary>
        private static bool IsRealModel(string model) =>
            !string.IsNullOrEmpty(model) && !model.StartsWith('<');

        /// <summary>表示用の短縮（"claude-opus-5" -> "opus-5"）</summary>
        private static string Shorten(string model) =>
            model.StartsWith("claude-", StringComparison.Ordinal) ? model["claude-".Length..] : model;

        /// <summary>
        /// 末尾のみを読む。1行=1JSON なので、全体をパースせず行単位で拾えば足りる。
        /// 書き込み中のファイルを掴むため ReadWrite 共有で開く。
        /// </summary>
        private static IReadOnlyList<string> ReadTailLines(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var start = Math.Max(0, fs.Length - TailBytes);
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[fs.Length - start];
                var read = fs.Read(buf, 0, buf.Length);
                var lines = Encoding.UTF8.GetString(buf, 0, read).Split('\n');
                // 途中から読んだ場合、先頭行は千切れているので捨てる
                return start > 0 && lines.Length > 1 ? lines[1..] : lines;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>先頭行（session_meta）だけを読む</summary>
        private static string? ReadHead(string path, int bytes = 8192)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buf = new byte[Math.Min(bytes, fs.Length)];
                var read = fs.Read(buf, 0, buf.Length);
                return Encoding.UTF8.GetString(buf, 0, read).Split('\n').FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }
    }
}
