namespace TerminalHub.Services;

/// <summary>
/// CLI を Windows コマンドプロンプト経由で起動するための引数を組み立てる。
/// ツール固有のオプション生成と、cmd.exe 固有の引用符規則を分離する。
/// </summary>
internal static class TerminalCommandLine
{
    internal static (string Command, string Arguments) KeepOpen(string executable, string? arguments)
    {
        var commandLine = Join(executable, arguments);
        return ("cmd.exe", $"/k {commandLine}");
    }

    /// <summary>
    /// 実行ファイルパスを引用符で囲んだうえで、引数があればコマンド全体をもう一組の引用符で囲む。
    /// **この二重の引用符は冗長に見えるが必須**。単純化すると下記の回帰を再発させる。
    ///
    /// cmd.exe /c は「引用符がちょうど2個」の条件を外れると、先頭の引用符と末尾の引用符
    /// だけを削る旧挙動になる（cmd /? 参照）。この形だと実行対象が "…\claude.cmd\"" のように
    /// パスの途中へ引用符が残り、「見つからない」ではなく ERROR_INVALID_NAME
    /// （ファイル名、ディレクトリ名、またはボリューム ラベルの構文が間違っています）で落ちる。
    /// v1.0.71 で --settings が常時付くようになり引用符が2個→4個になったことで、
    /// npm版の Claude Code セッションが起動できなくなっていた（4c22b53 / PR #170 で修正）。
    ///
    /// 「パスを囲んだうえで全体をもう一組で囲む」形なら、引用符付きの引数がいくつ増えても
    /// 壊れない（実測確認済み）。ネイティブ版(.exe)も npm版(.cmd)と同じ形にするのは、
    /// パスにスペースを含むユーザー名で同種の破綻を起こさないため
    /// （TerminalConstants.BuildClaudeCodeArgs の引用符と同じ理由）。
    /// </summary>
    internal static (string Command, string Arguments) ExecuteQuoted(string executablePath, string? arguments)
    {
        var quotedPath = $"\"{executablePath}\"";
        var commandLine = Join(quotedPath, arguments);
        var cmdArguments = string.IsNullOrWhiteSpace(arguments)
            ? $"/c {commandLine}"
            : $"/c \"{commandLine}\"";

        return ("cmd.exe", cmdArguments);
    }

    internal static (string Command, string Arguments) ResolveShell(
        IReadOnlyDictionary<string, string> options,
        string defaultShell)
    {
        return options.TryGetValue("command", out var command) && !string.IsNullOrWhiteSpace(command)
            ? (command, string.Empty)
            : (defaultShell, string.Empty);
    }

    private static string Join(string executable, string? arguments)
    {
        return string.IsNullOrWhiteSpace(arguments)
            ? executable
            : $"{executable} {arguments}";
    }
}
