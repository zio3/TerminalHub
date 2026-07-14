using TerminalHub.Constants;

namespace TerminalHub.Models;

/// <summary>
/// 起動済み Codex プロセスへ実際に渡した権限設定のスナップショット。
/// セッション設定を再起動せず保存しても、実行中プロセスの挙動判定が変わらないようにする。
/// </summary>
public sealed class CodexProcessOptionsSnapshot
{
    public bool PermissionRequestRequiresUserInput { get; }

    private CodexProcessOptionsSnapshot(bool permissionRequestRequiresUserInput)
    {
        PermissionRequestRequiresUserInput = permissionRequestRequiresUserInput;
    }

    public static CodexProcessOptionsSnapshot Capture(Dictionary<string, string> options)
        => new(TerminalConstants.CodexPermissionRequestRequiresUserInput(options));

    /// <summary>
    /// 起動済みプロセスの値を優先し、まだ起動していないセッションだけ保存中の設定から判定する。
    /// </summary>
    public static bool ResolvePermissionRequestRequiresUserInput(
        CodexProcessOptionsSnapshot? runningProcess,
        Dictionary<string, string> savedOptions)
        => runningProcess?.PermissionRequestRequiresUserInput ??
           TerminalConstants.CodexPermissionRequestRequiresUserInput(savedOptions);
}
