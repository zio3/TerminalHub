using System.Runtime.InteropServices;
using WtermPoC.Components;
using WtermPoC.Services;

// PoC: 子プロセス (ConPTY 配下の pwsh 等) が親 console を継承して直接書き込むのを防ぐ。
// FreeConsole で dotnet 自身の attached console を切り離すと、子プロセスは
// PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE で割り当てた pseudo console のみを使うようになる。
if (OperatingSystem.IsWindows())
{
    NativeMethods.FreeConsole();
}

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<IConPtyService, ConPtyService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeConsole();
}
