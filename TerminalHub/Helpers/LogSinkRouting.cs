using Serilog.Context;

namespace TerminalHub.Helpers
{
    /// <summary>
    /// 同じ出来事をコンソールとファイルで出し分けるための目印。
    ///
    /// 既定ではコンソールとファイルに同じイベントが流れる（Program.cs のシンク設定）。
    /// 起動コマンドラインのように「全文はファイルに残したいが、コンソールに出すと
    /// 画面が流れてしまう」ものだけ、要約版と全文版の2イベントに分けて片方ずつ送る。
    ///
    /// 目印は LogContext のプロパティとして載せる（Program.cs で Enrich.FromLogContext 済み）。
    /// シンク側は WriteTo.Conditional でこのプロパティの有無を見て振り分ける。
    /// </summary>
    public static class LogSinkRouting
    {
        /// <summary>このプロパティが付いたイベントはコンソールにだけ出す（ファイルへは出さない）。</summary>
        public const string ConsoleOnlyProperty = "TerminalHubConsoleOnly";

        /// <summary>このプロパティが付いたイベントはファイルにだけ出す（コンソールへは出さない）。</summary>
        public const string FileOnlyProperty = "TerminalHubFileOnly";

        /// <summary>using で囲んだ範囲のログをコンソール限定にする。</summary>
        public static IDisposable ConsoleOnly() => LogContext.PushProperty(ConsoleOnlyProperty, true);

        /// <summary>using で囲んだ範囲のログをファイル限定にする。</summary>
        public static IDisposable FileOnly() => LogContext.PushProperty(FileOnlyProperty, true);
    }
}
