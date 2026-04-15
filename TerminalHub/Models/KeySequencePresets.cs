namespace TerminalHub.Models
{
    /// <summary>
    /// カスタムコマンド (KeySequence) で送信できるプリセットキーの定義
    /// </summary>
    public static class KeySequencePresets
    {
        public record Preset(string DisplayName, string EscapeSequence);

        public static readonly System.Collections.Generic.IReadOnlyDictionary<string, Preset> All =
            new System.Collections.Generic.Dictionary<string, Preset>
            {
                ["CtrlC"]      = new("Ctrl+C",        "\x03"),
                ["CtrlD"]      = new("Ctrl+D",        "\x04"),
                ["CtrlL"]      = new("Ctrl+L (clear)","\x0C"),
                ["CtrlR"]      = new("Ctrl+R",        "\x12"),
                ["CtrlA"]      = new("Ctrl+A",        "\x01"),
                ["CtrlE"]      = new("Ctrl+E",        "\x05"),
                ["CtrlZ"]      = new("Ctrl+Z",        "\x1A"),
                ["Escape"]     = new("Esc",           "\x1B"),
                ["Tab"]        = new("Tab",           "\t"),
                ["ShiftTab"]   = new("Shift+Tab",     "\x1b[Z"),
                ["Enter"]      = new("Enter",         "\r"),
                ["ArrowUp"]    = new("↑",             "\x1b[A"),
                ["ArrowDown"]  = new("↓",             "\x1b[B"),
                ["ArrowRight"] = new("→",             "\x1b[C"),
                ["ArrowLeft"]  = new("←",             "\x1b[D"),
                ["Home"]       = new("Home",          "\x1b[H"),
                ["End"]        = new("End",           "\x1b[F"),
                ["AltM"]       = new("Alt+M",         "\x1Bm"),
            };

        /// <summary>キー名から表示名を取得（未定義なら null）</summary>
        public static string? GetDisplayName(string? keyName)
        {
            if (string.IsNullOrEmpty(keyName)) return null;
            return All.TryGetValue(keyName, out var preset) ? preset.DisplayName : null;
        }
    }
}
