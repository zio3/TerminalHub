using System;
using System.Collections.Generic;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    public interface IOutputAnalyzerService
    {
        void AnalyzeOutput(string data, SessionInfo sessionInfo, Guid activeSessionId, Action<Guid, string?>? updateStatus);
        void ResetSessionTimer(Guid sessionId);
        void StopSessionTimer(Guid sessionId);
        void SetTimeoutCallback(Action<Guid> timeoutCallback);

        /// <summary>
        /// データにアニメーションパターンが含まれている場合、タイマーをリセットする
        /// ジッター対策で部分的な更新が送られる場合のタイムアウト延長に使用
        /// </summary>
        /// <param name="data">ターミナルからの出力データ</param>
        /// <param name="sessionInfo">セッション情報</param>
        /// <returns>アニメーションパターンが検出されタイマーがリセットされた場合はtrue</returns>
        bool CheckAnimationPatternAndResetTimer(string data, SessionInfo sessionInfo);
    }
}