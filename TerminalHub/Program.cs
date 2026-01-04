using TerminalHub.Services;
using TerminalHub.Analyzers;
using TerminalHub.Components;
using TerminalHub.Models;
using System.Text.Json;

// CLI モードのチェック
if (args.Contains("--notify"))
{
    return await RunNotifyModeAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ConPtyService（Windows専用）
builder.Services.AddSingleton<IConPtyService, ConPtyService>();

// SessionManagerサービスを登録
builder.Services.AddSingleton<ISessionManager, SessionManager>();

// LocalStorageServiceを登録
builder.Services.AddScoped<ILocalStorageService, LocalStorageService>();

// NotificationServiceを登録
builder.Services.AddScoped<INotificationService, NotificationService>();

// GitServiceを登録
builder.Services.AddSingleton<IGitService, GitService>();

// OutputAnalyzerFactoryを登録
builder.Services.AddSingleton<IOutputAnalyzerFactory, OutputAnalyzerFactory>();

// TerminalServiceを登録
builder.Services.AddScoped<ITerminalService, TerminalService>();

// OutputAnalyzerServiceを登録
builder.Services.AddScoped<IOutputAnalyzerService, OutputAnalyzerService>();

// InputHistoryServiceを登録
builder.Services.AddScoped<IInputHistoryService, InputHistoryService>();

// PackageJsonServiceを登録
builder.Services.AddScoped<IPackageJsonService, PackageJsonService>();

// TaskManagerServiceを登録
builder.Services.AddScoped<ITaskManagerService, TaskManagerService>();

// ConPtyConnectionServiceを登録（Circuit毎のインスタンス）
builder.Services.AddScoped<ConPtyConnectionService>();

// HttpClientFactoryを登録（WebHook用）
builder.Services.AddHttpClient();

// WebhookSettingsServiceを登録（Singleton - ファイルベース）
builder.Services.AddSingleton<IWebhookSettingsService, WebhookSettingsService>();

// HookNotificationServiceを登録
builder.Services.AddSingleton<IHookNotificationService, HookNotificationService>();

// ClaudeHookServiceを登録
builder.Services.AddSingleton<IClaudeHookService, ClaudeHookService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();


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
