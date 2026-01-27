using TerminalHub.Services;
using TerminalHub.Analyzers;
using TerminalHub.Components;
using TerminalHub.Models;
using TerminalHub.Middleware;
using System.Text.Json;
using Serilog;

// CLI モードのチェック
if (args.Contains("--notify"))
{
    return await RunNotifyModeAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

// Serilog 設定
var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "terminalhub-.log");
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
var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var dbPath = Path.Combine(appDataPath, "TerminalHub", "sessions.db");
builder.Services.AddSingleton<SessionDbContext>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SessionDbContext>>();
    return new SessionDbContext(dbPath, logger);
});
builder.Services.AddSingleton<ISessionRepository, SessionRepository>();
builder.Services.AddScoped<IStorageServiceFactory, StorageServiceFactory>();

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

// HookNotificationServiceを登録
builder.Services.AddSingleton<IHookNotificationService, HookNotificationService>();

// ClaudeHookServiceを登録
builder.Services.AddSingleton<IClaudeHookService, ClaudeHookService>();

// ExternalAuthSettings を登録（外部アクセス時のBasic認証用）
// auth.json から読み込む（インストールフォルダに配置、更新時に上書きされない）
var authFilePath = Path.Combine(AppContext.BaseDirectory, "auth.json");
if (File.Exists(authFilePath))
{
    try
    {
        var authJson = File.ReadAllText(authFilePath);
        var authSettings = JsonSerializer.Deserialize<ExternalAuthSettings>(authJson);
        builder.Services.Configure<ExternalAuthSettings>(options =>
        {
            options.Username = authSettings?.Username;
            options.Password = authSettings?.Password;
        });
    }
    catch
    {
        builder.Services.Configure<ExternalAuthSettings>(_ => { });
    }
}
else
{
    builder.Services.Configure<ExternalAuthSettings>(_ => { });
}

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// HTTPSリダイレクトは無効化（ローカル環境での使用を想定）
// app.UseHttpsRedirection();

// 外部アクセス時のBasic認証（X-Forwarded-Forヘッダーがある場合のみ）
app.UseExternalAuth();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Hook 通知 API エンドポイント
app.MapPost("/api/hook", async (HookNotification notification, IHookNotificationService hookService) =>
{
    await hookService.HandleHookNotificationAsync(notification);
    return Results.Ok(new { success = true });
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
