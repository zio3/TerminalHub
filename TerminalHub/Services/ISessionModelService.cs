using TerminalHub.Models;

namespace TerminalHub.Services
{
    /// <summary>
    /// セッションが「実際に今どのモデルで動いているか」を CLI のトランスクリプトから取得する。
    /// 起動オプションではなく実測値を見るのは、/model による途中切り替えが頻繁に行われるため。
    /// </summary>
    public interface ISessionModelService
    {
        /// <summary>
        /// 実使用モデルの短縮名（例: "opus-5", "gpt-5.6-sol"）を取得する。
        /// 取得できない種別・記録が見つからない場合は null。
        /// </summary>
        /// <remarks>
        /// モデル名は「切り替え後に次に発話したとき」に初めて記録へ残るため、
        /// 切り替え直後は前の値が返る。これは仕様であり取得失敗ではない。
        /// </remarks>
        Task<string?> GetCurrentModelAsync(SessionInfo session);

        /// <summary>
        /// 複数セッション分をまとめて取得する。1件ずつ引くと同じ走査を人数分繰り返すため、
        /// 起動時など全件を埋めたい場面ではこちらを使う。
        /// </summary>
        Task<IReadOnlyDictionary<Guid, string?>> GetCurrentModelsAsync(IReadOnlyList<SessionInfo> sessions);

        /// <summary>
        /// キャッシュを捨てて次回に読み直させる。セッション再起動のように、
        /// 発話を待たずに値が変わりうる場面で使う。
        /// </summary>
        void Invalidate(Guid sessionId);
    }
}
