using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using TerminalHub.Services;

namespace TerminalHub.Helpers
{
    /// <summary>
    /// Host ヘッダを検査し、許可されていないホスト名からのリクエストを拒否するミドルウェア。
    ///
    /// 目的は DNS リバインディング対策。攻撃者は自分のドメインの DNS を一時的に 127.0.0.1 へ
    /// 向け替えることで、被害者のブラウザから TerminalHub へ「同一オリジンとして」到達できる。
    /// この経路はユーザー自身のブラウザ → ループバックなので、Tailscale や Cloudflare など
    /// 外側の認証はまったく通らない（＝外側へ委譲できない唯一の穴）。
    /// 成立すると画面の読み取りに加え、無認証の /mcp 経由でセッションへ文字列を送り込める。
    ///
    /// 判別できる材料は Host ヘッダしかない。攻撃者のリクエストは自分のドメイン名を名乗るので、
    /// 「このマシンが自分について知っている名前」だけを通せば塞げる。
    ///
    /// 標準の HostFilteringMiddleware を使わず自前で持つ理由:
    /// - 拒否時に素の 400 を返すだけで、何が起きたのか利用者に伝わらない。
    ///   このアプリは更新頻度が低く、リリースノートは読まれないまま更新されるため、
    ///   拒否ページが「設定が要る」と伝えられる事実上唯一の経路になる
    /// - 許可リストを appsettings.json ではなく app-settings.json (%LOCALAPPDATA%) から
    ///   読みたい。インストール先の appsettings.json は更新のたびに上書きされてしまう
    /// </summary>
    public static class HostFilter
    {
        /// <summary>
        /// 起動時に構築する、このマシン自身を指す名前の一覧。
        /// 保存はしない（毎起動で作り直す）。DHCP で LAN の IP が変わったり、
        /// Tailscale を入れ直したりしても、再起動すれば勝手に追従させるため。
        /// </summary>
        private static readonly HashSet<string> _selfHosts = BuildSelfHosts();

        private static HashSet<string> BuildSelfHosts()
        {
            var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "localhost",
                "127.0.0.1",
                "::1",
                "[::1]",
            };

            try
            {
                hosts.Add(Dns.GetHostName());
            }
            catch
            {
                // ホスト名が引けない環境でも、localhost 分だけで動作は続けられる
            }

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;

                    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        var ip = addr.Address;
                        if (ip.AddressFamily == AddressFamily.InterNetwork)
                        {
                            // LAN の 192.168.x / Tailscale の 100.x はここで拾える
                            hosts.Add(ip.ToString());
                        }
                        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            // Host ヘッダ上の IPv6 は角括弧付きで来る。スコープ ID は落とす
                            var plain = ip.ScopeId != 0
                                ? new IPAddress(ip.GetAddressBytes()).ToString()
                                : ip.ToString();
                            hosts.Add(plain);
                            hosts.Add($"[{plain}]");
                        }
                    }
                }
            }
            catch
            {
                // NIC の列挙に失敗しても localhost からは使える状態を保つ
            }

            return hosts;
        }

        /// <summary>現在有効な許可リスト（自動検出 + 設定の追加分）。診断・設定画面の表示用。</summary>
        public static IReadOnlyCollection<string> GetSelfHosts() => _selfHosts;

        public static IApplicationBuilder UseHostFilter(this IApplicationBuilder app)
        {
            var logger = app.ApplicationServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(HostFilter).FullName!);

            logger.LogInformation(
                "[HostFilter] 自動検出した自ホスト名: {Hosts}",
                string.Join(", ", _selfHosts.OrderBy(h => h)));

            return app.Use(async (context, next) =>
            {
                var settings = context.RequestServices
                    .GetRequiredService<IAppSettingsService>()
                    .GetSettings().Security;

                if (!settings.EnableHostFilter)
                {
                    await next();
                    return;
                }

                // Host ヘッダからポートを落として比較する。
                // HostString.Host は "[::1]:5080" のような IPv6 表記も正しく分解してくれる。
                var host = context.Request.Host.Host;

                if (string.IsNullOrEmpty(host) || IsAllowed(host, settings.AdditionalAllowedHosts))
                {
                    await next();
                    return;
                }

                logger.LogWarning(
                    "[HostFilter] 許可されていないホスト名からのリクエストを拒否しました: {Host} (path={Path})",
                    host, context.Request.Path);

                await WriteRejectionAsync(context, host);
            });
        }

        private static bool IsAllowed(string host, List<string> additional)
        {
            if (_selfHosts.Contains(host))
                return true;

            foreach (var extra in additional)
            {
                if (string.IsNullOrWhiteSpace(extra))
                    continue;

                // 設定欄にポート付きやスキーム付きで書かれても拾えるようにする
                // (利用者はブラウザのアドレスバーからコピーしてくる)
                if (string.Equals(Normalize(extra), host, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>設定欄の入力を Host ヘッダと比較できる形へ寄せる。</summary>
        public static string Normalize(string value)
        {
            var v = value.Trim();
            if (v.Length == 0)
                return v;

            var schemeIndex = v.IndexOf("//", StringComparison.Ordinal);
            if (schemeIndex >= 0)
                v = v[(schemeIndex + 2)..];

            var slash = v.IndexOf('/');
            if (slash >= 0)
                v = v[..slash];

            // IPv6 の角括弧内のコロンは残し、末尾のポートだけ落とす
            if (v.StartsWith('['))
            {
                var close = v.IndexOf(']');
                if (close >= 0)
                    return v[..(close + 1)];
            }

            var colon = v.LastIndexOf(':');
            if (colon > 0)
                v = v[..colon];

            return v;
        }

        private static async Task WriteRejectionAsync(HttpContext context, string host)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;

            // ブラウザのページ表示以外（SignalR / MCP / hook API）は素の 403 で十分。
            // HTML を返しても読む人がいないうえ、ログを汚す。
            var accept = context.Request.Headers.Accept.ToString();
            if (!accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                return;

            context.Response.ContentType = "text/html; charset=utf-8";

            // 許可リストの中身は出さない。ここに載せると、このマシンの LAN IP・ホスト名・
            // トンネルのドメインを攻撃者に教えることになる。
            // 出すのは拒否したホスト名だけで、これは相手が既に知っている情報。
            var safeHost = WebUtility.HtmlEncode(host);
            var html = $$"""
                <!DOCTYPE html>
                <html lang="ja">
                <head>
                  <meta charset="utf-8">
                  <meta name="viewport" content="width=device-width, initial-scale=1.0">
                  <title>TerminalHub - 接続できません</title>
                  <style>
                    body { font-family: "Noto Sans JP", -apple-system, BlinkMacSystemFont, sans-serif;
                           background: #0f172a; color: #f1f5f9; margin: 0;
                           display: flex; justify-content: center; padding: 2rem 1rem; }
                    main { max-width: 34rem; }
                    h1 { font-size: 1.25rem; margin: 0 0 1rem; }
                    p { line-height: 1.7; color: #cbd5e1; }
                    .host { display: block; margin: 1rem 0; padding: 0.75rem 1rem;
                            background: #1e293b; border-left: 3px solid #dc3545;
                            border-radius: 0.375rem; font-family: Consolas, monospace;
                            word-break: break-all; color: #f1f5f9; }
                    ol { line-height: 1.9; color: #cbd5e1; padding-left: 1.25rem; }
                    code { background: #1e293b; padding: 0.1rem 0.35rem; border-radius: 0.25rem; }
                    .note { font-size: 0.875rem; color: #94a3b8; margin-top: 1.5rem; }
                  </style>
                </head>
                <body>
                  <main>
                    <h1>このホスト名では接続できません</h1>
                    <p>TerminalHub は、あらかじめ許可されたホスト名からの接続だけを受け付けます。
                       悪意のあるサイトが、あなたのブラウザ経由で TerminalHub を操作するのを防ぐためです。</p>
                    <p>受け取ったホスト名:</p>
                    <span class="host">{{safeHost}}</span>
                    <p>この名前を使い続けるには、次の手順で許可してください。</p>
                    <ol>
                      <li><strong>TerminalHub が動いている PC 本体</strong>で <code>http://localhost:5080</code> を開く<br>
                          <span class="note">この端末からは設定できません（今まさに拒否されているため）</span></li>
                      <li>設定 → セキュリティ →「追加で許可するホスト名」に上記の名前を追加</li>
                      <li>保存して、この画面を再読み込み</li>
                    </ol>
                    <p class="note">心当たりが無いのにこの画面が出た場合、
                       他のサイトがあなたのブラウザから TerminalHub へアクセスしようとした可能性があります。
                       その場合は許可せず、そのまま閉じてください。</p>
                  </main>
                </body>
                </html>
                """;

            await context.Response.WriteAsync(html, Encoding.UTF8);
        }
    }
}
