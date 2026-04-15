using System.Collections.Generic;
using System.Linq;

namespace TerminalHub.Models
{
    /// <summary>
    /// カスタムコマンド (KeySequence) で送信できるプリセットキーの定義
    /// </summary>
    public static class KeySequencePresets
    {
        public record Preset(string DisplayName, string EscapeSequence);

        // 順序を保証するため配列で定義する (UI ドロップダウンの並びに直結)。
        // Dictionary のイテレーション順は仕様上不定なため、一覧表示用の型は List を使う。
        public static readonly IReadOnlyList<KeyValuePair<string, Preset>> All = new[]
        {
            new KeyValuePair<string, Preset>("CtrlC",      new("Ctrl+C",        "\x03")),
            new KeyValuePair<string, Preset>("CtrlD",      new("Ctrl+D",        "\x04")),
            new KeyValuePair<string, Preset>("CtrlL",      new("Ctrl+L (clear)","\x0C")),
            new KeyValuePair<string, Preset>("CtrlR",      new("Ctrl+R",        "\x12")),
            new KeyValuePair<string, Preset>("CtrlA",      new("Ctrl+A",        "\x01")),
            new KeyValuePair<string, Preset>("CtrlE",      new("Ctrl+E",        "\x05")),
            new KeyValuePair<string, Preset>("CtrlZ",      new("Ctrl+Z",        "\x1A")),
            new KeyValuePair<string, Preset>("Escape",     new("Esc",           "\x1B")),
            new KeyValuePair<string, Preset>("Tab",        new("Tab",           "\t")),
            new KeyValuePair<string, Preset>("ShiftTab",   new("Shift+Tab",     "\x1b[Z")),
            new KeyValuePair<string, Preset>("Enter",      new("Enter",         "\r")),
            new KeyValuePair<string, Preset>("ArrowUp",    new("↑",             "\x1b[A")),
            new KeyValuePair<string, Preset>("ArrowDown",  new("↓",             "\x1b[B")),
            new KeyValuePair<string, Preset>("ArrowRight", new("→",             "\x1b[C")),
            new KeyValuePair<string, Preset>("ArrowLeft",  new("←",             "\x1b[D")),
            new KeyValuePair<string, Preset>("Home",       new("Home",          "\x1b[H")),
            new KeyValuePair<string, Preset>("End",        new("End",           "\x1b[F")),
            new KeyValuePair<string, Preset>("AltM",       new("Alt+M",         "\x1Bm")),
        };

        // O(1) ルックアップ用の内部辞書 (All から自動生成されるので順序依存しない)
        private static readonly IReadOnlyDictionary<string, Preset> _lookup =
            All.ToDictionary(kv => kv.Key, kv => kv.Value);

        /// <summary>UI デフォルト選択用の先頭キー</summary>
        public static string DefaultKey => All[0].Key;

        /// <summary>プリセット取得</summary>
        public static bool TryGet(string? keyName, out Preset preset)
        {
            if (!string.IsNullOrEmpty(keyName) && _lookup.TryGetValue(keyName, out var found))
            {
                preset = found;
                return true;
            }
            preset = null!;
            return false;
        }

        /// <summary>プリセットの存在確認</summary>
        public static bool Contains(string? keyName)
            => !string.IsNullOrEmpty(keyName) && _lookup.ContainsKey(keyName);

        /// <summary>キー名から表示名を取得（未定義なら null）</summary>
        public static string? GetDisplayName(string? keyName)
            => TryGet(keyName, out var preset) ? preset.DisplayName : null;
    }
}
