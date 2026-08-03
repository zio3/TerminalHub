using System.Diagnostics;

namespace TerminalHub.Services;

/// <summary>
/// エクスプローラーでフォルダを開くサービス。
/// Blazor Server はバックグラウンドプロセスのため、素の Process.Start では
/// 開いた Explorer ウィンドウがブラウザの裏に隠れてしまう。
/// 起動後に新規ウィンドウを探して強制前面化する。
/// </summary>
public interface IExplorerLauncherService
{
    /// <summary>
    /// エクスプローラーで指定フォルダを開き、ウィンドウを前面に出す。フォルダが無ければ何もしない。
    /// 戻り値は「explorer.exe の起動まで成功したか」。前面化の成否は含まない（非同期のベストエフォート）。
    /// </summary>
    bool OpenFolder(string path);

    /// <summary>
    /// エクスプローラーで親フォルダを開き、指定したファイル/フォルダを選択状態にして前面に出す
    /// （explorer /select）。対象が実在しなければ何もしない。戻り値の意味は OpenFolder と同じ。
    /// </summary>
    bool OpenFolderAndSelect(string itemPath);
}

public class ExplorerLauncherService : IExplorerLauncherService
{
    // エクスプローラーのフォルダウィンドウのウィンドウクラス名
    private const string ExplorerWindowClass = "CabinetWClass";

    private readonly ILogger<ExplorerLauncherService> _logger;

    public ExplorerLauncherService(ILogger<ExplorerLauncherService> logger)
    {
        _logger = logger;
    }

    public bool OpenFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return false;

        return LaunchExplorer($"\"{path.Replace('/', '\\')}\"", path);
    }

    public bool OpenFolderAndSelect(string itemPath)
    {
        if (string.IsNullOrEmpty(itemPath) || (!File.Exists(itemPath) && !Directory.Exists(itemPath)))
            return false;

        // /select は親フォルダを開いて対象をハイライトする（ファイルにもフォルダにも使える）
        return LaunchExplorer($"/select,\"{itemPath.Replace('/', '\\')}\"", itemPath);
    }

    private bool LaunchExplorer(string arguments, string pathForLog)
    {
        try
        {
            // 起動前のウィンドウ一覧を控えておき、後で「新しく増えたウィンドウ」を特定する
            var before = ForegroundWindowHelper.FindVisibleWindowsByClass(ExplorerWindowClass).ToHashSet();

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true,
            });

            // Explorer ウィンドウは既存の explorer.exe プロセス側で開くため、
            // Process.Start の戻り値からはウィンドウを辿れない。
            // 新規の CabinetWClass ウィンドウが現れるのをポーリングして前面化する（最大3秒）。
            _ = Task.Run(async () =>
            {
                try
                {
                    for (var i = 0; i < 30; i++)
                    {
                        await Task.Delay(100);
                        var fresh = ForegroundWindowHelper.FindVisibleWindowsByClass(ExplorerWindowClass)
                            .FirstOrDefault(h => !before.Contains(h));
                        if (fresh != IntPtr.Zero)
                        {
                            ForegroundWindowHelper.ForceForeground(fresh);
                            return;
                        }
                    }
                    // 見つからなければ既存ウィンドウが再利用された等。従来どおり何もしない。
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ExplorerLauncher] ウィンドウ前面化に失敗: {Path}", pathForLog);
                }
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ExplorerLauncher] フォルダを開けませんでした: {Path}", pathForLog);
            return false;
        }
    }
}
