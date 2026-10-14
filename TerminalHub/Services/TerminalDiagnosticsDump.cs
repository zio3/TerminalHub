using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    /// <summary>
    /// ブラウザ側 xterm の「実際に見えている状態」。JS の <c>terminalFunctions.captureDiagnostics</c> が返す。
    /// サーバーのエミュレータが持つ画面と突き合わせ、崩れ・飛びがどちら側で起きたかを切り分ける材料。
    /// </summary>
    public sealed class TerminalClientDiagnostics
    {
        public bool Available { get; set; }
        public int Cols { get; set; }
        public int Rows { get; set; }
        /// <summary>表示中の先頭行（スクロールバック込みの行番号）。BaseY と一致していれば最下部に居る。</summary>
        public int ViewportY { get; set; }
        /// <summary>最下部に居るときの先頭行。Length - Rows に等しい。</summary>
        public int BaseY { get; set; }
        /// <summary>スクロールバック込みの総行数。</summary>
        public int Length { get; set; }
        public int CursorX { get; set; }
        public int CursorY { get; set; }
        /// <summary>"normal" / "alternate"。alt screen に居たかの判定用。</summary>
        public string? BufferType { get; set; }
        public int? ScrollbackOption { get; set; }
        public bool? HasWebgl { get; set; }
        public bool? Visible { get; set; }
        /// <summary>xterm の全行（スクロールバック込み）を平文にしたもの。装飾は落とす。</summary>
        public string? Text { get; set; }
        /// <summary>ブラウザ側の直近イベント履歴（スクロール・ホイール・書き込み・リサイズ）。そのまま書き出す。</summary>
        public JsonElement? Events { get; set; }
    }

    /// <summary>
    /// 「この画面を保存」の実体。稀に起きる表示の異常（スクロールが飛ぶ・崩れる）を、起きた瞬間に
    /// 1 操作で証拠として残すための診断ダンプ。ログフォルダ配下 <c>diag/</c> に 1 回 1 フォルダで書き出す。
    ///
    /// 同梱するもの:
    /// - emulator-replay.txt: サーバー側エミュレータの画面（切り替え時に xterm へ流す復元内容そのもの）
    /// - rawring.json: ConPTY から届いた生出力のリング（rawring-replay.html で単体再現できる形式）
    /// - xterm-text.txt: ブラウザ側 xterm の全行の平文
    /// - client-events.json: ブラウザ側の直近イベント履歴
    /// - state.json: 両側のサイズ・表示位置・行数などの数値と、セッションの状態
    ///
    /// <see cref="RawRingDump"/> と同じく診断機能なので、失敗しても本流へ例外を伝播しない。
    /// </summary>
    public static class TerminalDiagnosticsDump
    {
        /// <summary>保存先ルート（ログフォルダ）。Program.cs 起動時に設定する。未設定なら何もしない。</summary>
        public static string? LogsFolder { get; set; }

        private const string SubFolder = "diag";
        private const int MaxBundles = 20;
        private static readonly object _writeLock = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            // セッション名などの日本語を \uXXXX にせず読める形で出す
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// セッションの診断一式をフォルダに書き出し、そのパスを返す。失敗したら null。
        /// </summary>
        public static string? WriteBundle(SessionInfo session, TerminalClientDiagnostics? client, string reason)
        {
            try
            {
                if (string.IsNullOrEmpty(LogsFolder))
                {
                    return null;
                }

                var shortId = session.SessionId.ToString().Substring(0, 8);
                var root = Path.Combine(LogsFolder, SubFolder);
                lock (_writeLock)
                {
                    var folder = Path.Combine(root, $"diag-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{shortId}-{reason}");
                    Directory.CreateDirectory(folder);

                    File.WriteAllText(Path.Combine(folder, "emulator-replay.txt"), session.GetTerminalBuffer());

                    var export = RawRingExport.Build(session);
                    if (export != null)
                    {
                        File.WriteAllText(Path.Combine(folder, "rawring.json"), RawRingExport.Serialize(export));
                    }

                    if (client?.Text != null)
                    {
                        File.WriteAllText(Path.Combine(folder, "xterm-text.txt"), client.Text);
                    }
                    if (client?.Events is JsonElement events)
                    {
                        File.WriteAllText(Path.Combine(folder, "client-events.json"), JsonSerializer.Serialize(events, JsonOptions));
                    }

                    var (cols, rows) = session.TerminalBufferSize2D;
                    var state = new
                    {
                        capturedAt = DateTime.Now,
                        reason,
                        sessionId = session.SessionId,
                        name = session.GetDisplayName(),
                        terminalType = session.TerminalType.ToString(),
                        server = new
                        {
                            cols,
                            rows,
                            replayChars = session.TerminalBufferSize,
                            rawRingEntries = session.TerminalRawRing?.Count,
                            hasExited = session.ConPtySession?.HasExited,
                            processingStatus = session.ProcessingStatus,
                            isWaitingForUserInput = session.IsWaitingForUserInput,
                        },
                        client = client == null ? null : new
                        {
                            client.Available,
                            client.Cols,
                            client.Rows,
                            client.ViewportY,
                            client.BaseY,
                            client.Length,
                            // 最下部に居なければ true。スクロールが「上に置き去り」になっていたかの直接の証拠
                            scrolledUp = client.Available && client.ViewportY < client.BaseY,
                            client.CursorX,
                            client.CursorY,
                            client.BufferType,
                            client.ScrollbackOption,
                            client.HasWebgl,
                            client.Visible,
                            textChars = client.Text?.Length,
                        },
                    };
                    File.WriteAllText(Path.Combine(folder, "state.json"), JsonSerializer.Serialize(state, JsonOptions));

                    PruneOldBundles(root);
                    return folder;
                }
            }
            catch (Exception ex)
            {
                // 診断機能が本流を壊さないよう握り潰すが、保存失敗が不可視だと「証拠を残す」目的に反するためログには残す
                Log.Error(ex, "[Diag] 診断ダンプの保存に失敗: session={SessionId} reason={Reason}",
                    session.SessionId.ToString().Substring(0, 8), reason);
                return null;
            }
        }

        /// <summary>古いダンプフォルダを削除し、直近 <see cref="MaxBundles"/> 件だけ残す。</summary>
        private static void PruneOldBundles(string root)
        {
            var dirs = new DirectoryInfo(root).GetDirectories("diag-*")
                .OrderByDescending(d => d.Name)
                .Skip(MaxBundles);
            foreach (var d in dirs)
            {
                try { d.Delete(recursive: true); } catch { /* 掃除の失敗は無視 */ }
            }
        }
    }
}
