using System.Collections.Concurrent;
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
        private readonly IClaudeTranscriptLocator _transcriptLocator;

        // 直近の取得結果。短時間に何度も呼ばれても実ファイルを読み直さないための緩衝。
        // ロックは張らない（取得は読み取り専用で、同時に走っても結果は同じ。
        // 走査中に他セッションの取得を待たせる方が害が大きい）。
        private readonly ConcurrentDictionary<Guid, (string? Model, DateTime ReadAt)> _cache = new();

        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
        /// <summary>Codex の走査上限。全件でも数百程度だが、増え続ける前提で頭を抑える</summary>
        private const int CodexScanLimit = 500;
        /// <summary>末尾から読むバイト数。巨大な jsonl 全体を読むと極端に遅くなる</summary>
        private const int TailBytes = 256 * 1024;
        /// <summary>Claude で遡る記録ファイル数。再起動直後の空ファイルを飛ばすための保険</summary>
        private const int ClaudeFileLookback = 5;

        private static readonly Regex ModelRegex = new("\"model\":\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex CwdRegex = new("\"cwd\":\"([^\"]+)\"", RegexOptions.Compiled);

        public SessionModelService(ILogger<SessionModelService> logger, IClaudeTranscriptLocator transcriptLocator)
        {
            _logger = logger;
            _transcriptLocator = transcriptLocator;
        }

        public async Task<string?> GetCurrentModelAsync(SessionInfo session, bool forceRefresh = false)
        {
            if (!IsSupported(session)) return null;

            if (!forceRefresh && TryGetCached(session.SessionId, out var cached)) return cached;

            try
            {
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
        }

        public async Task<IReadOnlyDictionary<Guid, string?>> GetCurrentModelsAsync(IReadOnlyList<SessionInfo> sessions)
        {
            var targets = sessions.Where(IsSupported).ToList();
            var result = new Dictionary<Guid, string?>();
            if (targets.Count == 0) return result;

            var pending = new List<SessionInfo>();
            foreach (var s in targets)
            {
                if (TryGetCached(s.SessionId, out var cached)) result[s.SessionId] = cached;
                else pending.Add(s);
            }
            if (pending.Count == 0) return result;

            try
            {
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
            // 更新日時での足切りはしない。長く放置したセッションでもモデルは変わらず有効なため。
            // 再起動直後は新しい記録がまだ空で、最新ファイルだけ見るとバッジが消えてしまうので、
            // 見つかるまで新しい順に数件遡る（/model の指定は既定として引き継がれるため直前の値が妥当）
            foreach (var jsonl in _transcriptLocator.EnumerateNewestFirst(folderPath, ClaudeFileLookback))
            {
                // 末尾から遡り、最後に assistant が喋ったときのモデルを拾う
                foreach (var line in ReadTailLines(jsonl).Reverse())
                {
                    if (!line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal)) continue;
                    var m = ModelRegex.Match(line);
                    if (m.Success && IsRealModel(m.Groups[1].Value)) return Shorten(m.Groups[1].Value);
                }
            }
            return null;
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

        /// <summary>
        /// 表示用の短縮（"claude-opus-5" -> "opus-5"、
        /// "claude-haiku-4-5-20251001" -> "haiku-4-5"）。
        /// 日付入りのIDは一覧で幅を食うだけで、他のモデルと粒度も揃わないため落とす。
        /// </summary>
        private static string Shorten(string model)
        {
            var s = model.StartsWith("claude-", StringComparison.Ordinal) ? model["claude-".Length..] : model;
            return DateSuffixRegex.Replace(s, string.Empty);
        }

        private static readonly Regex DateSuffixRegex = new(@"-\d{8}$", RegexOptions.Compiled);

        private static IReadOnlyList<string> ReadTailLines(string path) =>
            TranscriptTail.ReadTailLines(path, TailBytes);

        private static string? ReadHead(string path) => TranscriptTail.ReadHead(path);
    }
}
