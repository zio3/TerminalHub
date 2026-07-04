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
                ForegroundWindowHelper.ForceForeground(owner.Handle);
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
            ForegroundWindowHelper.MakeTopmost(Handle);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                ForegroundWindowHelper.ClearTopmost(Handle);
                DestroyHandle();
            }
        }
    }
}
