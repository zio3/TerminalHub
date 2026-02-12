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
        Task DestroyTerminalAsync(Guid sessionId, bool showAlert = true);

        /// <summary>
        /// ターミナルを再作成する
        /// </summary>
        Task RecreateTerminalAsync(Guid sessionId, SessionInfo sessionInfo, ConPtySession? activeSession);

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
        /// ターミナルを最下部にスクロールする
        /// </summary>
        Task ScrollToBottomAsync(Guid sessionId);

        /// <summary>
        /// ターミナルにデータを書き込む
        /// </summary>
        Task WriteToTerminalAsync(IJSObjectReference terminal, string data);

        /// <summary>
        /// 要素が存在するかチェックする
        /// </summary>
        Task<bool> CheckElementExistsAsync(string elementId);

        /// <summary>
        /// ターミナルの表示状態を確実に設定する
        /// </summary>
        Task EnsureTerminalVisibleAsync(Guid sessionId);

        /// <summary>
        /// ターミナルの表示をリフレッシュする（バッファ復元後の表示更新用）
        /// </summary>
        Task RefreshTerminalAsync(Guid sessionId);
    }
}