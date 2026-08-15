using System.Collections.Concurrent;

namespace TerminalHub.Helpers
{
    /// <summary>
    /// Blazor Server の SignalR コネクションが long polling に落ちていないかを見張るミドルウェア。
    ///
    /// 背景: スリープ復帰や瞬断で WebSocket が切れると、SignalR は再接続時に long polling へ
    /// フォールバックすることがある。一度落ちると WebSocket に昇格し直さないため、そのコネクションが
    /// 生きている限り（実測で3時間以上）long polling のままになる。この状態はターミナル出力の
    /// 1バッチごとに HTTP 往復が発生するので体感が重くなり、リクエストログも大量に流れる。
    ///
    /// 判定: WebSocket なら /_blazor?id=... へのリクエストは接続あたり1本（アップグレードして
    /// 開きっぱなし）で終わる。素の HTTP リクエストが何十本も積み上がるのは long polling のときだけ。
    ///
    /// 現在は App.razor 側で transport を WebSocket に固定しているので、本来この警告は出ない。
    /// 古い HTML をキャッシュしたタブや、将来 Blazor 側の仕様が変わった場合の見張りとして残している
    /// （出たら固定が効いていないということ）。
    /// </summary>
    public static class SignalRTransportProbe
    {
        /// <summary>この本数を超えたら long polling と判断する。WebSocket 接続では到達しない。</summary>
        private const int PollingThreshold = 20;

        /// <summary>追跡中のコネクションがこの数を超えたら丸ごと捨てる（リークさせないため）。</summary>
        private const int MaxTrackedConnections = 64;

        private static readonly ConcurrentDictionary<string, int> _requestCounts = new();

        public static IApplicationBuilder UseSignalRTransportProbe(this IApplicationBuilder app)
        {
            var logger = app.ApplicationServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(SignalRTransportProbe).FullName!);

            return app.Use((context, next) =>
            {
                // negotiate や initializers は接続ごとに数回しか来ないので対象外。
                // WebSocket アップグレード要求も当然対象外。
                if (!context.Request.Path.StartsWithSegments("/_blazor", out var remaining) ||
                    remaining.HasValue ||
                    context.WebSockets.IsWebSocketRequest)
                {
                    return next();
                }

                var id = context.Request.Query["id"].ToString();
                if (string.IsNullOrEmpty(id))
                {
                    return next();
                }

                if (_requestCounts.Count > MaxTrackedConnections)
                {
                    _requestCounts.Clear();
                }

                var count = _requestCounts.AddOrUpdate(id, 1, (_, c) => c + 1);
                if (count == PollingThreshold)
                {
                    // 閾値ちょうどの1回だけ出す（以降はカウントが増え続けても黙る）。
                    logger.LogWarning(
                        "[SignalR] コネクション {ConnectionId} が long polling で動作している模様（/_blazor への素の HTTP リクエストが {Count} 本）。" +
                        "WebSocket へは自動復帰しないため、重い・ログが流れるようならブラウザをリロードして張り直すこと。",
                        id, count);
                }

                return next();
            });
        }
    }
}
