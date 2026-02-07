using System;
using System.Collections.Generic;
using System.Threading;

namespace TerminalHub.Services;

/// <summary>
/// セッションのタイムアウトタイマーを管理するサービス（Singleton）
/// </summary>
public interface ISessionTimerService
{
    /// <summary>
    /// セッションのタイマーをリセット（開始）する
    /// </summary>
    void ResetSessionTimer(Guid sessionId);

    /// <summary>
    /// セッションのタイマーを停止する
    /// </summary>
    void StopSessionTimer(Guid sessionId);

    /// <summary>
    /// タイムアウトコールバックを設定する
    /// </summary>
    void SetTimeoutCallback(Action<Guid> timeoutCallback);
}

/// <summary>
/// SessionTimerService の実装
/// </summary>
public class SessionTimerService : ISessionTimerService, IDisposable
{
    private readonly Dictionary<Guid, Timer> _sessionProcessingTimers = new();
    private readonly object _timerLock = new();
    private Action<Guid>? _timeoutCallback;
    private volatile bool _disposed;

    public void ResetSessionTimer(Guid sessionId)
    {
        if (_disposed) return;

        lock (_timerLock)
        {
            // 既存のタイマーを停止
            if (_sessionProcessingTimers.TryGetValue(sessionId, out var existingTimer))
            {
                existingTimer?.Dispose();
                _sessionProcessingTimers.Remove(sessionId);
            }

            // 新しいタイマーを作成（8秒後にタイムアウト）
            // 新しいClaude CodeフォーマットではTask一覧やステータスバーの描画で
            // スピナー文字を含まないチャンクが続く場合があるため余裕を持たせる
            var timer = new Timer(
                (state) => CheckSessionTimeout(sessionId),
                null,
                TimeSpan.FromSeconds(8),
                Timeout.InfiniteTimeSpan
            );

            _sessionProcessingTimers[sessionId] = timer;
        }
    }

    public void StopSessionTimer(Guid sessionId)
    {
        lock (_timerLock)
        {
            if (_sessionProcessingTimers.TryGetValue(sessionId, out var timer))
            {
                timer?.Dispose();
                _sessionProcessingTimers.Remove(sessionId);
            }
        }
    }

    public void SetTimeoutCallback(Action<Guid> timeoutCallback)
    {
        _timeoutCallback = timeoutCallback;
    }

    private void CheckSessionTimeout(Guid sessionId)
    {
        // Dispose後はコールバックを呼び出さない
        if (_disposed)
            return;

        _timeoutCallback?.Invoke(sessionId);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        lock (_timerLock)
        {
            // すべてのタイマーを停止
            foreach (var timer in _sessionProcessingTimers.Values)
            {
                timer?.Dispose();
            }
            _sessionProcessingTimers.Clear();
        }
    }
}
