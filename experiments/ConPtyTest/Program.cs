using ConPtyTest.Services;
using Microsoft.Extensions.Logging;

// ConPty 単体検証用 console アプリ。
// WinExe ビルドのため Console は出ないので、全ログは exe と同じディレクトリの
// datareceived.log に書き出す (= bin/Debug/net10.0-windows/datareceived.log)。

var logPath = Path.Combine(AppContext.BaseDirectory, "datareceived.log");
using var logFile = new StreamWriter(logPath, append: false) { AutoFlush = true };
var logLock = new object();

void Log(string msg)
{
    var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
    Console.WriteLine(line);
    lock (logLock)
    {
        logFile.WriteLine(line);
    }
}

Log($"=== ConPty 単体検証開始 (log: {logPath}) ===");
Log("[INFO] cmd.exe を ConPty で起動します");

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.IncludeScopes = false;
}).SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger<ConPtyService>();

var service = new ConPtyService(logger);
// 本体 TerminalHub の Terminal セッションと同じ条件: cmd.exe を直接、120x30
var session = await service.CreateSessionAsync("cmd.exe", null, null, 120, 30);

session.DataReceived += (s, e) =>
{
    var preview = e.Data.Replace("\x1b", "<ESC>").Replace("\r", "<CR>").Replace("\n", "<LF>\n");
    Log($"[DATA len={e.Data.Length}] {preview}");
};
session.ProcessExited += (s, e) =>
{
    Log("[EXITED]");
};

session.Start();
session.Resize(80, 24);  // 本体 SessionManager と同じく Start 直後に Resize
Log("[INFO] session.Start() + Resize(80,24) 完了。500ms 待機");
await Task.Delay(500);

// 本体は xterm.js の FitAddon が実サイズを計算してから 2 回目の Resize を呼ぶ。
// 違うサイズで Resize すると ConPty が画面再描画を子プロセスに要求する。
Log("[INFO] === 異なるサイズで 2 回目の Resize(120,30) ===");
session.Resize(120, 30);
await Task.Delay(2500);

Log("[INFO] === ver を送信 (cmd で動作する組込みコマンド) ===");
await session.WriteAsync("ver\r");
await Task.Delay(2000);

Log("[INFO] === exit を送信 ===");
await session.WriteAsync("exit\r");
await Task.Delay(2000);

Log("[INFO] session.Dispose()");
session.Dispose();
Log("=== 終了 ===");
