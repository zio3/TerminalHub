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

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint TopMostFlags = SWP_NOMOVE | SWP_NOSIZE;

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
