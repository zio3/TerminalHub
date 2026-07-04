using System.Runtime.InteropServices;
using System.Text;

namespace TerminalHub.Services;

/// <summary>
/// バックグラウンドプロセス(Blazor Server)から開いたウィンドウを前面化するための Win32 ヘルパー。
/// FolderPickerService(フォルダ選択ダイアログ) と ExplorerLauncherService(エクスプローラー) で共用する。
/// </summary>
internal static class ForegroundWindowHelper
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint TopMostFlags = SWP_NOMOVE | SWP_NOSIZE;
    private const int SW_SHOW = 5;

    /// <summary>ウィンドウを一時的に TOPMOST にする（位置・サイズは変えない）</summary>
    internal static void MakeTopmost(IntPtr hWnd)
        => SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, TopMostFlags);

    /// <summary>TOPMOST を解除して通常の Z オーダーに戻す（アクティブ化はしない）</summary>
    internal static void ClearTopmost(IntPtr hWnd)
        => SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, TopMostFlags | SWP_NOACTIVATE);

    /// <summary>
    /// 指定ウィンドウを強制的にフォアグラウンド化する。
    /// Blazor Server はバックグラウンドプロセスのため、単純な SetForegroundWindow は
    /// Windows のフォアグラウンドロックで拒否される。現在フォアグラウンドのウィンドウの
    /// 入力スレッドへ一時的に AttachThreadInput することで前面化を成功させる。
    /// </summary>
    internal static void ForceForeground(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;

        var foreground = GetForegroundWindow();
        uint foreThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        uint thisThread = GetCurrentThreadId();

        bool attached = false;
        try
        {
            if (foreThread != 0 && foreThread != thisThread)
            {
                attached = AttachThreadInput(foreThread, thisThread, true);
            }

            MakeTopmost(hWnd);
            ShowWindow(hWnd, SW_SHOW);
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
            // TOPMOST は前面化のための一時措置。アクティブ化後は解除して通常の Z オーダーに戻す。
            ClearTopmost(hWnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(foreThread, thisThread, false);
            }
        }
    }

    /// <summary>
    /// 指定ウィンドウクラスの可視トップレベルウィンドウを列挙する。
    /// エクスプローラーのフォルダウィンドウは "CabinetWClass"。
    /// </summary>
    internal static List<IntPtr> FindVisibleWindowsByClass(string className)
    {
        var result = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            if (IsWindowVisible(hWnd))
            {
                var sb = new StringBuilder(64);
                GetClassName(hWnd, sb, sb.Capacity);
                if (sb.ToString() == className)
                {
                    result.Add(hWnd);
                }
            }
            return true; // 列挙続行
        }, IntPtr.Zero);
        return result;
    }
}
