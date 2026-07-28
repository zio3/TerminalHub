using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    /// <summary>
    /// 生チャンクリングを「単体で再現試験できる」自己完結ファイルへ書き出す。
    /// 人間可読ダンプ（<see cref="RawRingDump"/>）と違い、こちらは機械可読・無損失で、
    /// TerminalHub 本体なしに <c>rawring-replay.html</c> だけで同じ絵を再現するのが目的。
    /// </summary>
    /// <remarks>
    /// 再現に不可欠なのは「データ」「チャンク境界」「到達間隔」に加えて<b>端末サイズ</b>である。
    /// 幅が違うと折り返し位置がずれ、本物と別種の崩れが出て調査を誤らせるため、
    /// サイズをファイルに同梱し、再生側はまずそのサイズへ合わせてから流す。
    /// </remarks>
    public static class RawRingExport
    {
        /// <summary>ファイル形式の識別子。互換性のない変更をしたら版を上げる。</summary>
        public const string FormatId = "terminalhub-rawring/1";

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            // 読み手は rawring-replay.html（JS）なので camelCase で出す。
            // 既定のままだと C# のプロパティ名そのまま（PascalCase）で出力され、
            // プレイヤーが entries/cols/data を見つけられず1件も読み込めない。
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // 生データには制御文字・日本語・記号が入る。既定のエスケープだと肥大するうえ
            // 目視での突き合わせができないので、非 ASCII はそのまま出す（JSON 文字列としては合法）
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>エクスポートの1エントリ。<see cref="Note"/> が非 null ならマーカー。</summary>
        /// <param name="Seq">リング内の通し番号（欠番があれば追い出し済み）</param>
        /// <param name="OffsetMs">最初のエントリからの経過ミリ秒。到達間隔の再現に使う</param>
        /// <param name="Data">生データ（エスケープしない。JSON 文字列としてそのまま）</param>
        /// <param name="Pending">取り込み直後のパーサ未確定文字列。非空＝シーケンス途中で切れたチャンク</param>
        /// <param name="Note">マーカー本文（BeginReplay / EndReplay / Clear / Resize NxM）</param>
        public sealed record ExportEntry(
            long Seq,
            double OffsetMs,
            string? Data,
            string? Pending,
            string? Note);

        /// <summary>エクスポートファイルの中身。</summary>
        public sealed record ExportFile(
            string Format,
            string SessionId,
            string SessionName,
            string TerminalType,
            int Cols,
            int Rows,
            string ExportedAtUtc,
            string? FirstEntryAtUtc,
            long TotalAdded,
            int EntryCount,
            int TotalChars,
            IReadOnlyList<ExportEntry> Entries);

        /// <summary>セッションのリングをエクスポート用のモデルへ変換する。リングが無ければ null。</summary>
        public static ExportFile? Build(SessionInfo session)
        {
            var ring = session.TerminalRawRing;
            if (ring == null)
            {
                return null;
            }

            var snapshot = ring.Snapshot();
            var origin = snapshot.Count > 0 ? snapshot[0].TimestampUtc : DateTime.UtcNow;
            var (cols, rows) = session.TerminalBufferSize2D;

            var entries = snapshot.Select(e => new ExportEntry(
                Seq: e.Seq,
                OffsetMs: Math.Round((e.TimestampUtc - origin).TotalMilliseconds, 3),
                Data: e.Note == null ? e.Data : null,
                Pending: e.Note == null && e.PendingAfter.Length > 0 ? e.PendingAfter : null,
                Note: e.Note)).ToArray();

            return new ExportFile(
                Format: FormatId,
                SessionId: session.SessionId.ToString(),
                SessionName: session.GetDisplayName(),
                TerminalType: session.TerminalType.ToString(),
                // 記録区間の途中で Resize が起きている場合、ここに入るのは「現在＝区間末尾」のサイズになる。
                // 区間の頭のサイズは Resize マーカーからしか辿れないので、再生側は
                // 「先頭に Resize マーカーがあればそれを優先し、無ければこの値で始める」こと。
                Cols: cols,
                Rows: rows,
                ExportedAtUtc: DateTime.UtcNow.ToString("o"),
                FirstEntryAtUtc: snapshot.Count > 0 ? origin.ToString("o") : null,
                TotalAdded: ring.TotalAdded,
                EntryCount: snapshot.Count,
                TotalChars: snapshot.Sum(e => e.Data.Length),
                Entries: entries);
        }

        /// <summary>エクスポートを JSON 文字列にする。</summary>
        public static string Serialize(ExportFile file)
        {
            return JsonSerializer.Serialize(file, SerializerOptions);
        }

        /// <summary>
        /// ダウンロード時のファイル名を作る。セッション名は日本語・記号を含みうるので入れず、
        /// 日時と GUID 先頭8桁だけにする（中身の SessionName で識別できる）。
        /// </summary>
        public static string BuildFileName(SessionInfo session)
        {
            var shortId = session.SessionId.ToString().Substring(0, 8);
            return $"rawring-{DateTime.Now:yyyyMMdd-HHmmss}-{shortId}.json";
        }
    }
}
