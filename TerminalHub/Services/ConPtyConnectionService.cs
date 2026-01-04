using System.Collections.Concurrent;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    /// <summary>
    /// ConPtySessionとクライアント（Circuit）間の接続を管理するサービス
    /// Scopedサービスとして登録され、各ブラウザ接続ごとにインスタンスが作成される
    /// </summary>
    public class ConPtyConnectionService : IDisposable
    {
        private readonly ILogger<ConPtyConnectionService> _logger;
        private readonly ConcurrentDictionary<Guid, ConPtySessionSubscription> _subscriptions = new();
        private bool _disposed;

        public ConPtyConnectionService(ILogger<ConPtyConnectionService> logger)
        {
            _logger = logger;
            _logger.LogInformation($"ConPtyConnectionService created for new Circuit");
        }

        /// <summary>
        /// ConPtySessionからのデータを受信したときに発生するイベント
        /// </summary>
        public event EventHandler<ConPtyDataReceivedEventArgs>? DataReceived;

        /// <summary>
        /// ConPtySessionのプロセスが終了したときに発生するイベント
        /// </summary>
        public event EventHandler<ConPtyProcessExitedEventArgs>? ProcessExited;

        /// <summary>
        /// 指定されたセッションのイベントを購読する
        /// </summary>
        public void SubscribeToSession(Guid sessionId, ConPtySession conPtySession)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ConPtyConnectionService));

            _logger.LogInformation($"Subscribing to session {sessionId}");

            // イベントハンドラーを作成
            EventHandler<DataReceivedEventArgs> dataHandler = (sender, args) =>
            {
                try
                {
                    DataReceived?.Invoke(this, new ConPtyDataReceivedEventArgs(sessionId, args.Data));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error in DataReceived event handler for session {sessionId}");
                }
            };

            EventHandler exitHandler = (sender, args) =>
            {
                try
                {
                    ProcessExited?.Invoke(this, new ConPtyProcessExitedEventArgs(sessionId));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error in ProcessExited event handler for session {sessionId}");
                }
            };

            // 購読情報を保存（TryAddの結果で既存チェック）
            var subscription = new ConPtySessionSubscription(
                sessionId,
                conPtySession,
                dataHandler,
                exitHandler
            );

            if (!_subscriptions.TryAdd(sessionId, subscription))
            {
                // 既に購読済みの場合はスキップ
                _logger.LogDebug($"Session {sessionId} is already subscribed");
                return;
            }

            // イベントハンドラーを登録（TryAdd成功後のみ）
            conPtySession.DataReceived += dataHandler;
            conPtySession.ProcessExited += exitHandler;

            _logger.LogInformation($"Successfully subscribed to session {sessionId}");
        }

        /// <summary>
        /// 指定されたセッションの購読を解除する
        /// </summary>
        public void UnsubscribeFromSession(Guid sessionId)
        {
            if (_subscriptions.TryRemove(sessionId, out var subscription))
            {
                _logger.LogInformation($"Unsubscribing from session {sessionId}");
                
                // イベントハンドラーを解除
                subscription.ConPtySession.DataReceived -= subscription.DataHandler;
                subscription.ConPtySession.ProcessExited -= subscription.ExitHandler;
                
                _logger.LogInformation($"Successfully unsubscribed from session {sessionId}");
            }
        }

        /// <summary>
        /// すべてのセッションの購読を解除する
        /// </summary>
        public void UnsubscribeAll()
        {
            _logger.LogInformation($"Unsubscribing from all {_subscriptions.Count} sessions");
            
            foreach (var sessionId in _subscriptions.Keys.ToList())
            {
                UnsubscribeFromSession(sessionId);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _logger.LogInformation("Disposing ConPtyConnectionService");
            UnsubscribeAll();
            _disposed = true;
        }

        /// <summary>
        /// 購読情報を保持する内部クラス
        /// </summary>
        private class ConPtySessionSubscription
        {
            public Guid SessionId { get; }
            public ConPtySession ConPtySession { get; }
            public EventHandler<DataReceivedEventArgs> DataHandler { get; }
            public EventHandler ExitHandler { get; }

            public ConPtySessionSubscription(
                Guid sessionId,
                ConPtySession conPtySession,
                EventHandler<DataReceivedEventArgs> dataHandler,
                EventHandler exitHandler)
            {
                SessionId = sessionId;
                ConPtySession = conPtySession;
                DataHandler = dataHandler;
                ExitHandler = exitHandler;
            }
        }
    }

    /// <summary>
    /// ConPtyからデータを受信したときのイベント引数
    /// </summary>
    public class ConPtyDataReceivedEventArgs : EventArgs
    {
        public Guid SessionId { get; }
        public string Data { get; }

        public ConPtyDataReceivedEventArgs(Guid sessionId, string data)
        {
            SessionId = sessionId;
            Data = data;
        }
    }

    /// <summary>
    /// ConPtyプロセスが終了したときのイベント引数
    /// </summary>
    public class ConPtyProcessExitedEventArgs : EventArgs
    {
        public Guid SessionId { get; }

        public ConPtyProcessExitedEventArgs(Guid sessionId)
        {
            SessionId = sessionId;
        }
    }
}