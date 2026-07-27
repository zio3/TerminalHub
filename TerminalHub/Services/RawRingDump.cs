using Serilog;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    /// <summary>
    /// 生チャンクリング（<see cref="TerminalHub.Terminal.RawChunkRing"/>）のダンプをログフォルダ配下へ
    /// ファイル書き出しする診断用ヘルパー。エミュレータ再同期（xterm 崩れが起きうる注入の瞬間）で
    /// 自動保存し、後から証拠区間がリングから流れてしまうのを防ぐ。
    /// </summary>
    public static class RawRingDump
    {
        /// <summary>ダンプの保存先ルート（ログフォルダ）。Program.cs 起動時に設定する。未設定なら何もしない。</summary>
        public static string? LogsFolder { get; set; }

        private const string SubFolder = "raw-ring";
        private const int MaxDumpFiles = 20;
        private static readonly object _writeLock = new();

        /// <summary>
        /// セッションのリング内容をファイルへ書き出し、保存先パスを返す。
        /// 診断用のため失敗しても呼び出し元へ例外を伝播しない（null を返す）。
        /// </summary>
        public static string? WriteDump(SessionInfo session, string reason)
        {
            try
            {
                var ring = session.TerminalRawRing;
                if (ring == null || string.IsNullOrEmpty(LogsFolder))
                {
                    return null;
                }
                var folder = Path.Combine(LogsFolder, SubFolder);
                lock (_writeLock)
                {
                    Directory.CreateDirectory(folder);
                    var shortId = session.SessionId.ToString().Substring(0, 8);
                    var path = Path.Combine(folder,
                        $"rawring-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{shortId}-{reason}.txt");
                    File.WriteAllText(path, ring.DumpText());
                    PruneOldDumps(folder);
                    return path;
                }
            }
            catch (Exception ex)
            {
                // 握り潰し自体は妥当（診断機能が本流を壊さない）だが、無言だと保存失敗が不可視になり
                // 「証拠を残す」という目的に反するため、失敗の事実だけはログに残す（レビュー指摘 #173）
                Log.Error(ex, "[RawRing] ダンプ保存に失敗: session={SessionId} reason={Reason}",
                    session.SessionId.ToString().Substring(0, 8), reason);
                return null;
            }
        }

        /// <summary>古いダンプを削除し、直近 <see cref="MaxDumpFiles"/> 件だけ残す。</summary>
        private static void PruneOldDumps(string folder)
        {
            var files = new DirectoryInfo(folder).GetFiles("rawring-*.txt")
                .OrderByDescending(f => f.Name)
                .Skip(MaxDumpFiles);
            foreach (var file in files)
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                    // 使用中等で消せなくても診断機能なので無視
                }
            }
        }
    }
}
