using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    /// <summary>
    /// セッションのコンテキスト量（トークン数）と、最後に記録が伸びた時刻。
    /// 時刻はプロンプトキャッシュの温度判定に使う（最後の API 要求から 1 時間で TTL 切れ）。
    /// </summary>
    /// <param name="Tokens">直近の assistant 発話時点のコンテキスト量</param>
    /// <param name="LastActivityUtc">トランスクリプトの最終更新時刻（UTC）</param>
    public record SessionContextUsage(int Tokens, DateTime LastActivityUtc);

    public interface ISessionContextService
    {
        /// <summary>
        /// 複数セッション分をまとめて取得する（対象外・取得不能は結果に含めない）。
        /// forceRefresh=true で緩衝を無視して読み直す（発話直後など、変わったと分かっているとき）。
        /// </summary>
        Task<IReadOnlyDictionary<Guid, SessionContextUsage>> GetContextUsagesAsync(
            IReadOnlyList<SessionInfo> sessions, bool forceRefresh = false);
    }

    /// <summary>
    /// Claude Code のトランスクリプト（.jsonl）末尾から、直近の assistant 発話の usage を読む。
    ///
    /// コンテキスト量 = input_tokens + cache_creation_input_tokens + cache_read_input_tokens。
    /// この3つがその発話で「読ませた全量」で、これがキャッシュに載る量でもある。
    /// output_tokens は次のターンの入力になるが、次の発話の usage に含まれて出てくるので足さない。
    ///
    /// Claude Code 限定。usage も .jsonl も Claude Code の形式で、他の CLI には無い。
    /// </summary>
    public class SessionContextService : ISessionContextService
    {
        private readonly ILogger<SessionContextService> _logger;
        private readonly IClaudeTranscriptLocator _transcriptLocator;

        // 取得はファイル読みなので、短時間に何度も呼ばれても読み直さないための緩衝。
        // 一覧の再描画は入力のたびに走るが、実ファイルを触るのは 30 秒に 1 回で足りる。
        private readonly ConcurrentDictionary<Guid, (SessionContextUsage? Usage, DateTime ReadAt)> _cache = new();

        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

        /// <summary>末尾から読むバイト数。1発話分の usage が入っていれば足りる</summary>
        private const int TailBytes = 128 * 1024;

        private static readonly Regex UsageRegex = new(
            "\"usage\":\\{(?<body>[^}]*)\\}", RegexOptions.Compiled);

        public SessionContextService(ILogger<SessionContextService> logger, IClaudeTranscriptLocator transcriptLocator)
        {
            _logger = logger;
            _transcriptLocator = transcriptLocator;
        }

        public async Task<IReadOnlyDictionary<Guid, SessionContextUsage>> GetContextUsagesAsync(
            IReadOnlyList<SessionInfo> sessions, bool forceRefresh = false)
        {
            var result = new Dictionary<Guid, SessionContextUsage>();
            var targets = sessions.Where(IsSupported).ToList();
            if (targets.Count == 0) return result;

            var pending = new List<SessionInfo>();
            foreach (var s in targets)
            {
                if (!forceRefresh && TryGetCached(s.SessionId, out var cached))
                {
                    if (cached != null) result[s.SessionId] = cached;
                }
                else
                {
                    pending.Add(s);
                }
            }
            if (pending.Count == 0) return result;

            try
            {
                await Task.Run(() =>
                {
                    var now = DateTime.UtcNow;
                    foreach (var s in pending)
                    {
                        var usage = Read(s);
                        _cache[s.SessionId] = (usage, now);
                        if (usage != null) result[s.SessionId] = usage;
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SessionContextService] コンテキスト量の取得に失敗");
            }
            return result;
        }

        private static bool IsSupported(SessionInfo session) =>
            session.TerminalType == TerminalType.ClaudeCode
            && (!string.IsNullOrEmpty(session.TranscriptPath) || !string.IsNullOrEmpty(session.FolderPath));

        private bool TryGetCached(Guid sessionId, out SessionContextUsage? usage)
        {
            if (_cache.TryGetValue(sessionId, out var e) && DateTime.UtcNow - e.ReadAt < CacheTtl)
            {
                usage = e.Usage;
                return true;
            }
            usage = null;
            return false;
        }

        private SessionContextUsage? Read(SessionInfo session)
        {
            var path = ResolveTranscript(session);
            if (path == null) return null;

            try
            {
                var lastWrite = File.GetLastWriteTimeUtc(path);

                // 末尾から遡り、最初に見つかった「本編の assistant 発話」の usage を採る。
                // サブエージェント（isSidechain:true）の発話は別コンテキストなので飛ばす
                // ——拾ってしまうと、Task 実行中だけ数字が small に化ける。
                foreach (var line in TranscriptTail.ReadTailLines(path, TailBytes).Reverse())
                {
                    if (!line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal)) continue;
                    if (line.Contains("\"isSidechain\":true", StringComparison.Ordinal)) continue;

                    var m = UsageRegex.Match(line);
                    if (!m.Success) continue;

                    var body = m.Groups["body"].Value;
                    var tokens = ReadInt(body, "input_tokens")
                        + ReadInt(body, "cache_creation_input_tokens")
                        + ReadInt(body, "cache_read_input_tokens");
                    if (tokens <= 0) continue;

                    return new SessionContextUsage(tokens, lastWrite);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[SessionContextService] 読み取りに失敗: {Path}", path);
            }
            return null;
        }

        /// <summary>
        /// hook が知らせてきたパスがあればそれを使う（同じフォルダに複数セッションがいても取り違えない）。
        /// まだ hook が飛んでいないセッションは、フォルダから最新の記録を推測する。
        /// </summary>
        private string? ResolveTranscript(SessionInfo session)
        {
            if (!string.IsNullOrEmpty(session.TranscriptPath) && File.Exists(session.TranscriptPath))
                return session.TranscriptPath;

            // 最新の1件だけ見る。モデル名と違って古い記録の値は無意味（前のセッションの残り）なので遡らない
            return _transcriptLocator.EnumerateNewestFirst(session.FolderPath, 1).FirstOrDefault();
        }

        /// <summary>usage 本体（"a":1,"b":2 …）から数値を1つ取り出す。無ければ 0</summary>
        private static int ReadInt(string body, string key)
        {
            var needle = $"\"{key}\":";
            var i = body.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return 0;

            i += needle.Length;
            var end = i;
            while (end < body.Length && char.IsAsciiDigit(body[end])) end++;
            return end > i && int.TryParse(body.AsSpan(i, end - i), out var v) ? v : 0;
        }
    }
}
