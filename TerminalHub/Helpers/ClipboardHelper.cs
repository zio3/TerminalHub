using System.Runtime.InteropServices;

namespace TerminalHub.Helpers;

/// <summary>
/// Win32 APIを使用してクリップボードの内容を判定するヘルパー
/// </summary>
public static class ClipboardHelper
{
    private const uint CF_BITMAP = 2;
    private const uint CF_DIB = 8;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    /// <summary>
    /// クリップボードに画像が含まれているかを判定する
    /// </summary>
    public static bool ContainsImage()
    {
        return IsClipboardFormatAvailable(CF_BITMAP) || IsClipboardFormatAvailable(CF_DIB);
    }
}
