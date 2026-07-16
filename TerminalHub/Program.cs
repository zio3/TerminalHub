using TerminalHub.Services;
using TerminalHub.Analyzers;
using TerminalHub.Components;
using TerminalHub.Models;
using System.Text.Json;
using Serilog;

// CLI モードのチェック
if (args.Contains("--notify"))
{
    return await RunNotifyModeAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

// Serilog 設定
// ユーザーデータ配下 (%LOCALAPPDATA%\TerminalHub\) にログを置く。Program Files にインストールした
// 場合の書き込み権限エラーを回避するため。dev/prod は AppDataPaths 側で切り分け。
var logsFolder = AppDataPaths.GetLogsFolder(
    builder.Environment.IsDevelopment(),
    builder.Configuration.GetValue<string>("Logging:FolderName"));
var logPath = Path.Combine(logsFolder, "terminalhub-.log");
builder.Host.UseSerilog((context, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(
            logPath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            fileSizeLimitBytes: 10 * 1024 * 1024, // 10MB
            rollOnFileSizeLimit: true,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
});

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ローカライゼーション設定
// - リソース配置: TerminalHub/Resources/SharedResource.{en,ja}.resx
// - 言語判定: Cookie (.AspNetCore.Culture) → Accept-Language → デフォルト "en"
// - 対応外言語は "en" にフォールバック (英語を canonical として統一)
//
// 注意: ResourcesPath は敢えて指定しない。
// .NET SDK のビルドが resx を埋め込む際、観測された実名は
//   TerminalHub.SharedResource.{culture}.resources
// となっていて "Resources." フォルダ prefix が付与されていない。
// ResourcesPath を指定すると localizer は "TerminalHub.Resources.SharedResource..." を
// 探しに行って見つけられずキー文字列がそのまま返ってしまう。
// 空のまま = typeInfo.FullName (TerminalHub.SharedResource) をそのまま prefix として使うので一致する。
builder.Services.AddLocalization();
builder.Services.Configure<Microsoft.AspNetCore.Builder.RequestLocalizationOptions>(options =>
{
    var supported = new[] { "en", "ja" };
    options.SetDefaultCulture("en");
    options.AddSupportedCultures(supported);
    options.AddSupportedUICultures(supported);
});

// SignalRのメッセージサイズ制限を増加（デフォルト32KBでは不足）
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 1024 * 1024; // 1MB
});

// ConPtyService（Windows専用）
builder.Services.AddSingleton<IConPtyService, ConPtyService>();

// SessionManagerサービスを登録
builder.Services.AddSingleton<ISessionManager, SessionManager>();

// LocalStorageServiceを登録
builder.Services.AddScoped<ILocalStorageService, LocalStorageService>();

// SQLiteセッションストレージを登録
// DB パスは logs / app-settings と同様に IsDevelopment で既定を切り替える
// (dev=sessions-dev.db / prod=sessions.db)。Database:FileName 設定があればそれを優先。
// これにより appsettings.Development.json が無い環境 (別 worktree 等) の Development 実行でも
// 本番 DB (sessions.db) を誤って触らない。
var dbPath = AppDataPaths.GetDatabaseFilePath(
    builder.Environment.IsDevelopment(),
    builder.Configuration.GetValue<string>("Database:FileName"));
builder.Logging.AddFilter("TerminalHub.Services.SessionDbContext", LogLevel.Debug);
builder.Services.AddSingleton<SessionDbContext>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SessionDbContext>>();
    return new SessionDbContext(dbPath, logger);
});
builder.Services.AddSingleton<ISessionRepository, SessionRepository>();
builder.Services.AddSingleton<ISessionMemoSnapshotRepository, SessionMemoSnapshotRepository>();
builder.Services.AddSingleton<ISessionMemoRepository, SessionMemoRepository>();
builder.Services.AddScoped<IStorageServiceFactory, StorageServiceFactory>();

// メモ編集履歴の自動スナップショットサービス (10分毎)
builder.Services.AddSingleton<MemoSnapshotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MemoSnapshotService>());

// NotificationServiceを登録
builder.Services.AddScoped<INotificationService, NotificationService>();

// GitServiceを登録
builder.Services.AddSingleton<IGitService, GitService>();

// OutputAnalyzerFactoryを登録
builder.Services.AddSingleton<IOutputAnalyzerFactory, OutputAnalyzerFactory>();

// SessionTimerServiceを登録（Singleton: セッションタイマーを一元管理）
builder.Services.AddSingleton<ISessionTimerService, SessionTimerService>();

// TerminalServiceを登録
builder.Services.AddScoped<ITerminalService, TerminalService>();

// OutputAnalyzerServiceを登録
builder.Services.AddScoped<IOutputAnalyzerService, OutputAnalyzerService>();

// InputHistoryServiceを登録
builder.Services.AddScoped<IInputHistoryService, InputHistoryService>();

// ConPtyConnectionServiceを登録（Circuit毎のインスタンス）
builder.Services.AddScoped<ConPtyConnectionService>();

// HttpClientFactoryを登録（WebHook用）
builder.Services.AddHttpClient();

// AppSettingsServiceを登録（Singleton - ファイルベース）
builder.Services.AddSingleton<IAppSettingsService, AppSettingsService>();

// FolderPickerServiceを登録
builder.Services.AddSingleton<IFolderPickerService, FolderPickerService>();

// エクスプローラー起動（前面化付き）を登録
builder.Services.AddSingleton<IExplorerLauncherService, ExplorerLauncherService>();

// リモート起動サービスを登録
builder.Services.AddSingleton<IRemoteLaunchService, RemoteLaunchService>();
builder.Services.AddSingleton<MqttService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttService>());

// HookNotificationServiceを登録
builder.Services.AddSingleton<IHookNotificationService, HookNotificationService>();

// ClaudeHookServiceを登録
builder.Services.AddSingleton<IClaudeHookService, ClaudeHookService>();

// CodexHookServiceを登録（起動引数用 lifecycle hook の生成）
builder.Services.AddSingleton<ICodexHookService, CodexHookService>();

// McpConfigServiceを登録（試験機能: 各CLIへ terminalhub MCP を繋ぐための起動オプションを用意する）
builder.Services.AddSingleton<IMcpConfigService, McpConfigService>();

// VersionCheckServiceを登録
builder.Services.AddSingleton<IVersionCheckService, VersionCheckService>();

// 生ストリームキャプチャ（VTエミュレータ検証フィクスチャ採取用のデバッグサービス）
builder.Services.AddSingleton<IRawStreamCaptureService, RawStreamCaptureService>();

// スラッシュコマンド補完の候補ソース（headless init から動的取得＋静的辞書フォールバック）
builder.Services.AddSingleton<SlashCommandProvider>();

// MCP サーバー（セッション間メッセージング）。
// TerminalHub 本体プロセスに HTTP MCP を同居させ、list_sessions / send_to_session / set_memo を公開する。
// SessionManager(Singleton) に直結するため HTTP トランスポート一択（stdio だと別プロセスで共有状態に届かない）。
//
// instructions（取扱説明・運用ルール）は ConfigureSessionOptions で「接続セッションごと」に設定から
// 読み込む。これにより設定を編集しても TerminalHub を再起動せず、各セッションが次に MCP へ接続し直した
// タイミング（例: CLI 側の /clear）で新しい instructions が反映される。
// （IOptions<McpServerOptions>.Value はシングルトンで1回キャッシュされ再起動が要るが、
//  ConfigureSessionOptions を設定すると SDK が接続ごとに options を生成し直すため動的反映になる）。
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.ConfigureSessionOptions = (httpContext, serverOptions, cancellationToken) =>
        {
            var appSettings = httpContext.RequestServices.GetRequiredService<IAppSettingsService>();
            var text = appSettings.GetSettings().Experimental.McpInstructions;
            serverOptions.ServerInstructions = string.IsNullOrWhiteSpace(text)
                ? TerminalHub.Mcp.McpInstructionDefaults.Template
                : text;
            return Task.CompletedTask;
        };
    })
    .WithTools<TerminalHub.Mcp.SessionMessagingTools>();


var app = builder.Build();

// dev/prod で保存先が切り替わるため、どのDBを開いたかを起動時に残す
app.Logger.LogInformation("[DB] 使用するDB: Environment={Environment}, FullPath={DbPath}",
    app.Environment.EnvironmentName, dbPath);

// ターミナル状態バッファ（VTエミュレータ）の初期グリッドサイズを設定
// （SessionInfo 作成時に TerminalStateBufferFactory.Create() が参照する）
{
    var appSettings = app.Services.GetRequiredService<IAppSettingsService>().GetSettings();
    TerminalHub.Terminal.TerminalStateBufferFactory.DefaultCols =
        app.Configuration.GetValue<int>("SessionSettings:DefaultCols", TerminalHub.Constants.TerminalConstants.DefaultCols);
    TerminalHub.Terminal.TerminalStateBufferFactory.DefaultRows =
        app.Configuration.GetValue<int>("SessionSettings:DefaultRows", TerminalHub.Constants.TerminalConstants.DefaultRows);

    // 生ストリームキャプチャの永続設定も起動時に反映（設定画面を開くまで効かない、を避ける）
    app.Services.GetRequiredService<IRawStreamCaptureService>().SetEnabled(appSettings.DevTools.CaptureRawStream);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// HTTPSリダイレクトは無効化（ローカル環境での使用を想定）
// app.UseHttpsRedirection();

app.UseAntiforgery();

// リクエストローカライゼーション
// Program.cs 先頭で登録した RequestLocalizationOptions を適用する。
// Cookie (.AspNetCore.Culture) → Accept-Language の順で culture を判定、対応外は "en" へフォールバック。
app.UseRequestLocalization();

// Cookie スライディング更新: culture cookie の有効期限を毎リクエスト 1 年先へ延長する。
// これでユーザーが最後にアクセスしてから 1 年放置しない限り言語選択は永続化される。
//
// 実装注意: await next() の後に Response.Cookies.Append を呼ぶと、静的ファイル配信や
// SignalR ストリーミング応答では既にヘッダーが送信済みで "Headers are read-only" 例外になる。
// OnStarting コールバックはヘッダー送信「直前」に一度だけ呼ばれるのでここで cookie を追記する。
app.Use((context, next) =>
{
    context.Response.OnStarting(() =>
    {
        // 実リクエストで解決された culture を IRequestCultureFeature から取得する。
        // CurrentUICulture は AsyncLocal で OnStarting 発火時に壊れているケースがあるので、
        // より信頼できる Feature 経由で取り直す。
        var feature = context.Features.Get<Microsoft.AspNetCore.Localization.IRequestCultureFeature>();
        var culture = feature?.RequestCulture ??
                      new Microsoft.AspNetCore.Localization.RequestCulture(System.Globalization.CultureInfo.CurrentUICulture);
        var cookieValue = Microsoft.AspNetCore.Localization.CookieRequestCultureProvider.MakeCookieValue(culture);
        context.Response.Cookies.Append(
            Microsoft.AspNetCore.Localization.CookieRequestCultureProvider.DefaultCookieName,
            cookieValue,
            new Microsoft.AspNetCore.Http.CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                HttpOnly = false, // 言語切替のため JS からも読み書き可能にする
                Path = "/"
            });
        return Task.CompletedTask;
    });
    return next();
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Hook 通知 API エンドポイント（汎用形式: TerminalHub 自作 JSON、--notify CLI モードや他ツール向けに維持）
app.MapPost("/api/hook", async (HookNotification notification, IHookNotificationService hookService) =>
{
    await hookService.HandleHookNotificationAsync(notification);
    return Results.Ok(new { success = true });
});

// Hook 通知 API エンドポイント（Claude Code 専用: type:"http" hook が直接送信するネイティブ JSON を受信）
// TerminalHub のセッションIDは URL パスから取得する（Claude Code の session_id は Claude 側のIDで別物のため）
app.MapPost("/api/hook/claude/{sessionId:guid}",
    async (Guid sessionId, ClaudeHookPayload payload, IHookNotificationService hookService) =>
{
    // 既存 HookNotification 形式へ変換し、既存サービスに委譲
    var notification = new HookNotification
    {
        SessionId = sessionId,
        Event = payload.HookEventName ?? "",
        AgentId = payload.AgentId,
        AgentType = payload.AgentType,
        Message = payload.Message,
        ToolName = payload.ToolName,
        Timestamp = DateTime.UtcNow
    };
    await hookService.HandleHookNotificationAsync(notification);
    // Claude Code 仕様: 2xx 空ボディ = 成功扱い。JSON を返すと構造化判定として解析されるため空で返す。
    return Results.NoContent();
});

// Hook 通知 API エンドポイント（Codex CLI 専用: ブリッジ(TerminalHub.exe --notify --source codex)が
// Codex のネイティブ JSON を stdin で受け取り、ここへ転送してくる）
// TerminalHub のセッションIDは URL パスから取得する（Codex の session_id は別物のため）。
// Codex の hook イベント名は Claude とほぼ共通なので、HookNotification に正規化して同じ処理へ流す。
app.MapPost("/api/hook/codex/{sessionId:guid}",
    async (Guid sessionId, ClaudeHookPayload payload, IHookNotificationService hookService) =>
{
    var notification = new HookNotification
    {
        SessionId = sessionId,
        Event = payload.HookEventName ?? "",
        AgentId = payload.AgentId,
        AgentType = payload.AgentType,
        Message = payload.Message,
        ToolName = payload.ToolName,
        Timestamp = DateTime.UtcNow
    };
    await hookService.HandleHookNotificationAsync(notification);
    return Results.NoContent();
});

// MCP エンドポイント（/mcp）。Claude Code 等の MCP クライアントがここへ接続する。
app.MapMcp("/mcp");

app.Run();
return 0;

// CLI モード: Hook 通知を送信
static async Task<int> RunNotifyModeAsync(string[] args)
{
    // Codex CLI ブリッジモード: Codex の lifecycle hook(type:command) から起動され、
    // stdin で渡された Codex ネイティブ JSON を /api/hook/codex/{sessionId} へ転送する。
    if (string.Equals(GetArgValue(args, "--source"), "codex", StringComparison.OrdinalIgnoreCase))
    {
        return await RunCodexBridgeAsync(args);
    }

    var eventType = GetArgValue(args, "--event") ?? "";
    var sessionIdStr = GetArgValue(args, "--session") ?? "";
    var portStr = GetArgValue(args, "--port") ?? "5081";

    if (string.IsNullOrEmpty(eventType) || string.IsNullOrEmpty(sessionIdStr))
    {
        // "PermissionRequest" は過去の CLI 互換のためヘルプに残置しているのみ。
        // 実際に HookNotification.GetEventType が受理するのは
        // Stop / UserPromptSubmit / Notification の 3 種だけで、
        // PermissionRequest を渡しても null 扱いになる。
        Console.Error.WriteLine("Usage: TerminalHub.exe --notify --event <Stop|UserPromptSubmit|PermissionRequest> --session <sessionId> [--port <port>]");
        return 1;
    }

    if (!Guid.TryParse(sessionIdStr, out var sessionId))
    {
        Console.Error.WriteLine($"Invalid session ID: {sessionIdStr}");
        return 1;
    }

    if (!int.TryParse(portStr, out var port))
    {
        port = 5081;
    }

    var notification = new HookNotification
    {
        Event = eventType,
        SessionId = sessionId,
        Timestamp = DateTime.UtcNow
    };

    try
    {
        using var handler = new HttpClientHandler
        {
            // 自己署名証明書を許可（ローカル開発用）
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
        };
        using var client = new HttpClient(handler);
        // 5 秒固定。Claude Code の hook timeout (ClaudeHookService.BuildHookEntry) と揃える。
        // 長くすると Claude Code 側が hook の完了を待つ分、Stop / UserPromptSubmit 等の
        // 発火に遅延が乗り、ユーザー体感の処理完了通知が遅れるため短めを維持。
        client.Timeout = TimeSpan.FromSeconds(5);

        var json = JsonSerializer.Serialize(notification);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        // まず HTTP を試す
        try
        {
            var response = await client.PostAsync($"http://localhost:{port}/api/hook", content);
            if (response.IsSuccessStatusCode)
            {
                return 0;
            }
        }
        catch
        {
            // HTTP 失敗時は HTTPS を試す
        }

        // HTTPS を試す
        content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var httpsResponse = await client.PostAsync($"https://localhost:{port}/api/hook", content);

        if (httpsResponse.IsSuccessStatusCode)
        {
            return 0;
        }
        else
        {
            Console.Error.WriteLine($"Failed to send notification: {httpsResponse.StatusCode}");
            return 1;
        }
    }
    catch (Exception ex)
    {
        // 接続失敗時はサイレントに終了（hook 実行をブロックしない）
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 0; // 成功扱いで終了
    }
}

// CLI モード（Codex ブリッジ）: stdin の Codex JSON を /api/hook/codex/{sessionId} へ素通し転送する。
static async Task<int> RunCodexBridgeAsync(string[] args)
{
    var sessionIdStr = GetArgValue(args, "--session") ?? "";
    var portStr = GetArgValue(args, "--port") ?? "5081";

    if (!Guid.TryParse(sessionIdStr, out var sessionId))
    {
        Console.Error.WriteLine($"Invalid session ID: {sessionIdStr}");
        return 0; // hook をブロックしないため成功扱い
    }
    if (!int.TryParse(portStr, out var port)) port = 5081;

    // Codex は hook の JSON を stdin で渡す。全部読み取って素通し転送する。
    // Console.In は日本語 Windows だと CP932 になりうるため、stdin を明示的に UTF-8 で読む
    // （非ASCII（日本語 message 等）の文字化け・二重再エンコードを防ぐ）。
    string body;
    using (var reader = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8))
    {
        body = await reader.ReadToEndAsync();
    }
    if (string.IsNullOrWhiteSpace(body)) return 0;

    try
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (m, c, ch, e) => true
        };
        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(5);

        var path = $"/api/hook/codex/{sessionId}";
        // まず HTTP、ダメなら HTTPS（Claude ブリッジと同じフォールバック）。
        // http → https の順で試す。起動引数へ注入するブリッジ起動コマンド (CodexHookService.BuildBridgeCommand)
        // は --port しか渡さず、スキームを伝えていない。TerminalHub 本体は既定では HTTP のみで listen する
        // が、SessionManager.GetServerBaseUrl は「HTTPS しかなければ HTTPS を使う」構成も想定しているため、
        // HTTPS-only で起動された場合はこのフォールバックが唯一の到達手段になる。
        // 「本体は HTTP しか listen していないから https 分岐は死んでいる」と判断して消さないこと。
        foreach (var scheme in new[] { "http", "https" })
        {
            try
            {
                using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                var res = await client.PostAsync($"{scheme}://localhost:{port}{path}", content);
                if (res.IsSuccessStatusCode) return 0;
            }
            catch { /* 次の scheme を試す */ }
        }
        return 0; // 失敗しても hook はブロックしない
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 0;
    }
}

static string? GetArgValue(string[] args, string key)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }
    return null;
}
