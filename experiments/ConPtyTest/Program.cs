using ConPtyTest.Services;
using Microsoft.Extensions.Logging;

// ConPty 単体検証用 console アプリ。
// 目的: ConPtyService が pwsh.exe を起動したとき、子プロセスの全出力 (バナー、プロンプト、Get-Date 結果など) が
//       DataReceived イベント経由で届くかどうかを観察する。
// 期待動作: pwsh のバナー / Import-Module エラー / プロンプト / Get-Date 出力が `[DATA] ...` プレフィックス付きで表示される。
// 不具合発生時: 同じ出力が `[DATA]` なしの素のテキストとして console に漏れる (= ConPty パイプではなく親 stdout に流れている)。

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.IncludeScopes = false;
}).SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger<ConPtyService>();

Console.WriteLine("=== ConPty 単体検証開始 ===");
Console.WriteLine("[INFO] pwsh.exe -NoLogo を ConPty で起動します");

var service = new ConPtyService(logger);
// 本体 TerminalHub の Terminal セッションと同じ条件: cmd.exe を直接渡す
// → ConPtyService 内部で `cmd.exe /c cmd.exe` (二重 cmd) になる
var session = await service.CreateSessionAsync("cmd.exe", null, null, 80, 24);

session.DataReceived += (s, e) =>
{
    var preview = e.Data.Replace("\x1b", "<ESC>").Replace("\r", "<CR>").Replace("\n", "<LF>\n");
    Console.WriteLine($"[DATA len={e.Data.Length}] {preview}");
};
session.ProcessExited += (s, e) =>
{
    Console.WriteLine("[EXITED]");
};

session.Start();
session.Resize(80, 24);  // 本体 SessionManager と同じく Start 直後に Resize
Console.WriteLine("[INFO] session.Start() + Resize 完了。3 秒待機します");
await Task.Delay(3000);

Console.WriteLine("[INFO] === ver を送信 (cmd で動作する組込みコマンド) ===");
await session.WriteAsync("ver\r");
await Task.Delay(2000);

Console.WriteLine("[INFO] === exit を送信 ===");
await session.WriteAsync("exit\r");
await Task.Delay(2000);

Console.WriteLine("[INFO] session.Dispose()");
session.Dispose();
Console.WriteLine("=== 終了 ===");
