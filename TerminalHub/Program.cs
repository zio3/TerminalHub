using TerminalHub.Services;
using TerminalHub.Analyzers;
using TerminalHub.Components;

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

// HttpClientFactoryを登録（WebHook用）
builder.Services.AddHttpClient();

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

app.Run();
