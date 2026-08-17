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
        /// <summary>Claude のプロジェクト一覧（直接検索が外れたときの保険）を作り直す間隔</summary>
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
            if (!IsSupported(session)) return null;

            await _lock.WaitAsync();
            try
            {
                if (TryGetCached(session.SessionId, out var cached)) return cached;

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

        public async Task<IReadOnlyDictionary<Guid, string?>> GetCurrentModelsAsync(IReadOnlyList<SessionInfo> sessions)
        {
            var targets = sessions.Where(IsSupported).ToList();
            var result = new Dictionary<Guid, string?>();
            if (targets.Count == 0) return result;

            await _lock.WaitAsync();
            try
            {
                var pending = new List<SessionInfo>();
                foreach (var s in targets)
                {
                    if (TryGetCached(s.SessionId, out var cached)) result[s.SessionId] = cached;
                    else pending.Add(s);
                }
                if (pending.Count == 0) return result;

                await Task.Run(() =>
                {
                    // Claude は 1 セッション = 1 ディレクトリ直接参照で済むので個別に引く
                    foreach (var s in pending.Where(s => s.TerminalType == TerminalType.ClaudeCode))
                    {
                        result[s.SessionId] = ReadClaudeModel(s.FolderPath);
                    }

                    // Codex は cwd がファイルの中にしかなく、1 件ずつ引くと同じ走査を人数分繰り返すため、
                    // 新しい順に1回だけ舐めて、全員分が揃った時点で打ち切る
                    var codex = pending.Where(s => s.TerminalType == TerminalType.CodexCLI).ToList();
                    if (codex.Count > 0)
                    {
                        foreach (var (id, model) in ResolveCodexBatch(codex)) result[id] = model;
                    }
                });

                var now = DateTime.UtcNow;
                foreach (var s in pending)
                {
                    _cache[s.SessionId] = (result.GetValueOrDefault(s.SessionId), now);
                }
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SessionModelService] モデルの一括取得に失敗");
                return result;
            }
            finally
            {
                _lock.Release();
            }
        }

        private static bool IsSupported(SessionInfo session) =>
            !string.IsNullOrEmpty(session.FolderPath)
            && (session.TerminalType == TerminalType.ClaudeCode || session.TerminalType == TerminalType.CodexCLI);

        private bool TryGetCached(Guid sessionId, out string? model)
        {
            if (_cache.TryGetValue(sessionId, out var e) && DateTime.UtcNow - e.ReadAt < CacheTtl)
            {
                model = e.Model;
                return true;
            }
            model = null;
            return false;
        }

        // --- Claude Code -----------------------------------------------------
        // ~/.claude/projects/<記号を - に潰した cwd>/<uuid>.jsonl

        private string? ReadClaudeModel(string folderPath)
        {
            var dir = FindClaudeProjectDir(folderPath);
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

        /// <summary>
        /// フォルダ名は cwd から機械的に決まるので、まず組み立てて直接叩く（全列挙は不要）。
        /// Windows のパス比較は大文字小文字を区別しないため、記録側と綴りが違っても直接ヒットする。
        /// 外れたときだけ、保険として一覧から突き合わせる。
        /// </summary>
        private string? FindClaudeProjectDir(string folderPath)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (!Directory.Exists(root)) return null;

            var key = ToProjectKey(folderPath);
            if (key == null) return null;

            var direct = Path.Combine(root, key);
            if (Directory.Exists(direct)) return direct;

            return GetClaudeProjectIndex(root).GetValueOrDefault(key);
        }

        private Dictionary<string, string>? _claudeIndex;
        private DateTime _claudeIndexAt;

        private Dictionary<string, string> GetClaudeProjectIndex(string root)
        {
            if (_claudeIndex != null && DateTime.UtcNow - _claudeIndexAt < IndexTtl) return _claudeIndex;

            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var k = ToProjectKey(Path.GetFileName(dir));
                if (k != null) index[k] = dir;
            }
            _claudeIndex = index;
            _claudeIndexAt = DateTime.UtcNow;
            return index;
        }

        /// <summary>パス -> projects のフォルダ名。末尾の空白や区切りは落としてから記号を潰す</summary>
        private static string? ToProjectKey(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var p = path.Trim().TrimEnd('\\', '/', ' ');
            var sb = new StringBuilder(p.Length);
            foreach (var c in p)
                sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
            return sb.ToString().TrimEnd('-');
        }

        // --- Codex -----------------------------------------------------------
        // ~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl（先頭行の session_meta に cwd が生パスで入る）

        private string? ReadCodexModel(string folderPath)
        {
            var target = NormalizePath(folderPath);
            foreach (var file in EnumerateRolloutsNewestFirst())
            {
                if (MatchCodexCwd(file, target) is not string path) continue;
                return ReadCodexModelFromFile(path);
            }
            return null;
        }

        /// <summary>
        /// 複数セッション分をまとめて解決する。新しい順に1回舐め、全員分が埋まったら打ち切る。
        /// </summary>
        private Dictionary<Guid, string?> ResolveCodexBatch(List<SessionInfo> sessions)
        {
            var result = sessions.ToDictionary(s => s.SessionId, _ => (string?)null);

            // 同じフォルダに複数セッションがぶら下がることがあるのでまとめて持つ
            var wanted = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sessions)
            {
                var key = NormalizePath(s.FolderPath);
                if (!wanted.TryGetValue(key, out var ids)) wanted[key] = ids = new List<Guid>();
                ids.Add(s.SessionId);
            }

            foreach (var file in EnumerateRolloutsNewestFirst())
            {
                if (wanted.Count == 0) break; // 全員分そろったので、残りは見ない

                var head = ReadHead(file);
                if (head == null) continue;
                if (head.Contains("\"thread_source\":\"subagent\"", StringComparison.Ordinal)) continue;

                var cwd = CwdRegex.Match(head);
                if (!cwd.Success) continue;

                var recorded = NormalizePath(cwd.Groups[1].Value.Replace("\\\\", "\\"));
                if (!wanted.TryGetValue(recorded, out var ids)) continue;

                var model = ReadCodexModelFromFile(file);
                foreach (var id in ids) result[id] = model;
                wanted.Remove(recorded);
            }
            return result;
        }

        /// <summary>先頭行の cwd が target と一致すればそのパスを返す。サブエージェントの記録は除外</summary>
        private string? MatchCodexCwd(string file, string target)
        {
            var head = ReadHead(file);
            if (head == null) return null;

            // サブエージェントの記録には、モデル名ではない値（審査エージェント名等）が入るため除外する
            if (head.Contains("\"thread_source\":\"subagent\"", StringComparison.Ordinal)) return null;

            var cwd = CwdRegex.Match(head);
            if (!cwd.Success) return null;

            var recorded = NormalizePath(cwd.Groups[1].Value.Replace("\\\\", "\\"));
            return string.Equals(recorded, target, StringComparison.OrdinalIgnoreCase) ? file : null;
        }

        private string? ReadCodexModelFromFile(string path)
        {
            foreach (var line in ReadTailLines(path).Reverse())
            {
                var m = ModelRegex.Match(line);
                if (m.Success && IsRealModel(m.Groups[1].Value)) return Shorten(m.Groups[1].Value);
            }
            return null;
        }

        private static IEnumerable<string> EnumerateRolloutsNewestFirst()
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
            if (!Directory.Exists(root)) return Array.Empty<string>();

            return Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(CodexScanLimit)
                .Select(f => f.FullName);
        }

        // --- 共通 -------------------------------------------------------------

        private static string NormalizePath(string path) => path.Trim().TrimEnd('\\', '/', ' ');

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
