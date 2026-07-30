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
    /// パスを引用符で囲み、cmd.exe /c に渡すコマンド全体も必要に応じて引用符で囲む。
    /// 引数内に引用符が増えても cmd.exe が実行ファイルパスの引用符を誤って除去しない形にする。
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
