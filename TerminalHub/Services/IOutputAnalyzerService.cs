using System;
using System.Collections.Generic;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    public interface IOutputAnalyzerService
    {
        void AnalyzeOutput(string data, SessionInfo sessionInfo, Guid activeSessionId, Action<Guid, string?> updateStatus);
        void ResetSessionTimer(Guid sessionId);
        void StopSessionTimer(Guid sessionId);
        void SetTimeoutCallback(Action<Guid> timeoutCallback);
    }
}