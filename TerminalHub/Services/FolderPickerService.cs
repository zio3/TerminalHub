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

                var result = dialog.ShowDialog();
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
}
