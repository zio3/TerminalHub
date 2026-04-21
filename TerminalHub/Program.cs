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
var dbFileName = builder.Configuration.GetValue<string>("Database:FileName") ?? "sessions.db";
var dbPath = Path.Combine(AppDataPaths.UserDataRoot, dbFileName);
builder.Logging.AddFilter("TerminalHub.Services.SessionDbContext", LogLevel.Debug);
Console.WriteLine($"[DB][起動時診断] 使用するDB: Environment={builder.Environment.EnvironmentName} / FileName={dbFileName} / FullPath={dbPath}");
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

// リモート起動サービスを登録
builder.Services.AddSingleton<IRemoteLaunchService, RemoteLaunchService>();
builder.Services.AddSingleton<MqttService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttService>());

// HookNotificationServiceを登録
builder.Services.AddSingleton<IHookNotificationService, HookNotificationService>();

// ClaudeHookServiceを登録
builder.Services.AddSingleton<IClaudeHookService, ClaudeHookService>();

// VersionCheckServiceを登録
builder.Services.AddSingleton<IVersionCheckService, VersionCheckService>();


var app = builder.Build();

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
        Timestamp = DateTime.UtcNow
    };
    await hookService.HandleHookNotificationAsync(notification);
    // Claude Code 仕様: 2xx 空ボディ = 成功扱い。JSON を返すと構造化判定として解析されるため空で返す。
    return Results.NoContent();
});

app.Run();
return 0;

// CLI モード: Hook 通知を送信
static async Task<int> RunNotifyModeAsync(string[] args)
{
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
