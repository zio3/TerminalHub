using System.Runtime.InteropServices;

namespace TerminalHub.Services;

/// <summary>
/// Windowsネイティブのフォルダ選択ダイアログを表示するサービス
/// </summary>
public interface IFolderPickerService
{
    /// <summary>
    /// フォルダ選択ダイアログを表示し、選択されたパスを返す。
    /// キャンセルされた場合はnullを返す。
    /// </summary>
    Task<string?> PickFolderAsync(string? initialDirectory = null);
}

public class FolderPickerService : IFolderPickerService
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

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint TopMostFlags = SWP_NOMOVE | SWP_NOSIZE;
    private const int SW_SHOW = 5;

    /// <summary>
    /// 指定ウィンドウを強制的にフォアグラウンド化する。
    /// Blazor Server はバックグラウンドプロセスのため、単純な SetForegroundWindow は
    /// Windows のフォアグラウンドロックで拒否される。現在フォアグラウンドのウィンドウの
    /// 入力スレッドへ一時的に AttachThreadInput することで前面化を成功させる。
    /// ShowDialog(owner) で開くモーダルの IFileDialog はオーナーに追従して前面に出る。
    /// </summary>
    private static void ForceForeground(IntPtr hWnd)
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

            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, TopMostFlags);
            ShowWindow(hWnd, SW_SHOW);
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
            // TOPMOST は前面化のための一時措置。アクティブ化後は解除して通常の Z オーダーに戻す。
            SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, TopMostFlags | SWP_NOACTIVATE);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(foreThread, thisThread, false);
            }
        }
    }

    public Task<string?> PickFolderAsync(string? initialDirectory = null)
    {
        var tcs = new TaskCompletionSource<string?>();

        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    ShowNewFolderButton = true
                };

                if (!string.IsNullOrEmpty(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
                {
                    dialog.InitialDirectory = initialDirectory;
                }

                // ダイアログの親として一瞬TOPMOSTにするオーナーウィンドウを作成。
                // Blazor Serverはバックグラウンドプロセスのため、
                // そのままではWindowsがダイアログを前面に出さない。
                using var owner = new TopmostOwnerWindow();
                // オーナーを強制的にフォアグラウンド化してから ShowDialog する。
                // こうしないと App Mode(Chrome) などが前面のとき、ダイアログが裏に回ってしまう。
                ForceForeground(owner.Handle);
                var result = dialog.ShowDialog(owner);
                tcs.SetResult(result == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    /// <summary>
    /// ダイアログ表示時に一瞬TOPMOSTにして前面表示を保証するオーナーウィンドウ。
    /// ShowDialog(owner) に渡すと、ダイアログもオーナーの Z-order に従って前面に出る。
    /// </summary>
    private sealed class TopmostOwnerWindow : System.Windows.Forms.NativeWindow, IDisposable
    {
        public TopmostOwnerWindow()
        {
            CreateHandle(new System.Windows.Forms.CreateParams
            {
                X = 0, Y = 0, Width = 0, Height = 0,
                Style = 0x00000000, // WS_OVERLAPPED (不可視)
            });
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, TopMostFlags);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                SetWindowPos(Handle, HWND_NOTOPMOST, 0, 0, 0, 0, TopMostFlags | SWP_NOACTIVATE);
                DestroyHandle();
            }
        }
    }
}
