using System.Collections.Concurrent;
using System.Text;

namespace TerminalHub.Services
{
    /// <summary>
    /// 作業フォルダから Claude Code のトランスクリプト（~/.claude/projects/&lt;cwd を潰した名前&gt;/*.jsonl）を引く。
    ///
    /// SessionModelService と SessionContextService が同じ引き方をするため共通化した。
    /// hook の transcript_path を受け取る案もあるが、それだと hook が最低1回飛ぶまで
    /// 紐付けが得られない。フォルダ名から機械的に決まる以上、こちらの方が起動直後から使える。
    /// </summary>
    public interface IClaudeTranscriptLocator
    {
        /// <summary>新しい順にトランスクリプトのパスを返す（存在しなければ空）</summary>
        IReadOnlyList<string> EnumerateNewestFirst(string folderPath, int take);
    }

    public class ClaudeTranscriptLocator : IClaudeTranscriptLocator
    {
        /// <summary>プロジェクト一覧（直接検索が外れたときの保険）を作り直す間隔</summary>
        private static readonly TimeSpan IndexTtl = TimeSpan.FromMinutes(2);

        // 複数スレッドから同時に読まれるので、作りかけを見せないよう完成品を丸ごと差し替える。
        // 同時に作り直しが走っても結果は同じなので、作成自体は直列化しない。
        private volatile ProjectIndex? _index;

        private sealed record ProjectIndex(Dictionary<string, string> Map, DateTime BuiltAt);

        public IReadOnlyList<string> EnumerateNewestFirst(string folderPath, int take)
        {
            var dir = FindProjectDir(folderPath);
            if (dir == null) return Array.Empty<string>();

            try
            {
                return Directory.EnumerateFiles(dir, "*.jsonl")
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(take)
                    .Select(f => f.FullName)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// フォルダ名は cwd から機械的に決まるので、まず組み立てて直接叩く（全列挙は不要）。
        /// Windows のパス比較は大文字小文字を区別しないため、記録側と綴りが違っても直接ヒットする。
        /// 外れたときだけ、保険として一覧から突き合わせる。
        /// </summary>
        private string? FindProjectDir(string folderPath)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (!Directory.Exists(root)) return null;

            var key = ToProjectKey(folderPath);
            if (key == null) return null;

            var direct = Path.Combine(root, key);
            if (Directory.Exists(direct)) return direct;

            return GetIndex(root).GetValueOrDefault(key);
        }

        private Dictionary<string, string> GetIndex(string root)
        {
            var current = _index;
            if (current != null && DateTime.UtcNow - current.BuiltAt < IndexTtl) return current.Map;

            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var k = ToProjectKey(Path.GetFileName(dir));
                if (k != null) index[k] = dir;
            }
            _index = new ProjectIndex(index, DateTime.UtcNow);
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
    }

    /// <summary>
    /// jsonl（1行=1JSON）を必要な分だけ読むための共通処理。
    /// 巨大なファイルを丸ごと読むと極端に遅くなるので、末尾/先頭だけを掴む。
    /// </summary>
    public static class TranscriptTail
    {
        /// <summary>
        /// 末尾のみを読む。書き込み中のファイルを掴むため ReadWrite 共有で開く。
        /// </summary>
        public static IReadOnlyList<string> ReadTailLines(string path, int tailBytes) =>
            ReadTailLines(path, tailBytes, out _);

        /// <summary>
        /// 末尾のみを読む。捨てた千切れ行を <paramref name="droppedHead"/> で返す。
        /// 呼び出し側は、そこに欲しい情報の痕跡があれば読む量を増やして読み直せる
        /// （1行が読み取り幅を超えると、その行＝最新の記録を丸ごと取りこぼすため）。
        /// </summary>
        public static IReadOnlyList<string> ReadTailLines(string path, int tailBytes, out string? droppedHead)
        {
            droppedHead = null;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var start = Math.Max(0, fs.Length - tailBytes);
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[fs.Length - start];
                var read = fs.Read(buf, 0, buf.Length);
                var lines = Encoding.UTF8.GetString(buf, 0, read).Split('\n');
                // 途中から読んだ場合、先頭行は千切れているので捨てる
                if (start > 0 && lines.Length > 1)
                {
                    droppedHead = lines[0];
                    return lines[1..];
                }
                return lines;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>先頭行だけを読む</summary>
        public static string? ReadHead(string path, int bytes = 8192)
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
