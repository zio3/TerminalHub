using Microsoft.JSInterop;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    public interface ITerminalService
    {
        /// <summary>
        /// ターミナルを初期化する
        /// </summary>
        Task<IJSObjectReference?> InitializeTerminalAsync(Guid sessionId, DotNetObjectReference<object> dotNetRef, int fontSize = 14);

        /// <summary>
        /// ターミナルを破棄する
        /// </summary>
        Task DestroyTerminalAsync(Guid sessionId);

        /// <summary>
        /// すべてのターミナルを非表示にする
        /// </summary>
        Task HideAllTerminalsAsync();

        /// <summary>
        /// 指定されたターミナルを表示する
        /// </summary>
        Task ShowTerminalAsync(Guid sessionId);

        /// <summary>
        /// ターミナルのリサイズを実行する
        /// </summary>
        Task ResizeTerminalAsync(IJSObjectReference terminal);

        /// <summary>
        /// xterm の現在のサイズ（cols/rows）を取得する。取得できない場合は null。
        /// </summary>
        Task<(int Cols, int Rows)?> GetTerminalSizeAsync(IJSObjectReference terminal);

        /// <summary>
        /// ターミナルにデータを書き込む
        /// </summary>
        Task WriteToTerminalAsync(IJSObjectReference terminal, string data);

        /// <summary>
        /// ターミナルに大きなデータをチャンク分割して書き込む。
        /// チャンク間で await して Blazor サーキットの Dispatcher に制御を返すため、
        /// 巨大なリプレイ書き込み中でも送信などの UI 操作が割り込めるようになる。
        /// </summary>
        Task WriteToTerminalChunkedAsync(IJSObjectReference terminal, string data, int chunkSize = 32768);

        /// <summary>
        /// 要素が存在するかチェックする
        /// </summary>
        Task<bool> CheckElementExistsAsync(string elementId);

        /// <summary>
        /// ターミナルの表示をリフレッシュする（バッファ復元後の表示更新用）
        /// </summary>
        Task RefreshTerminalAsync(Guid sessionId);
    }
}