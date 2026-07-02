namespace TerminalHub.Terminal;

/// <summary>
/// コードポイントの表示幅（East Asian Width ベース）。
/// xterm.js の既定（Ambiguous=半角）に合わせる。0=結合文字等, 1=半角, 2=全角。
/// </summary>
public static class CharWidth
{
    /// <summary>コードポイントの表示セル幅を返す。</summary>
    public static int GetWidth(int cp)
    {
        // 制御文字は幅0扱い（グリッドには入れない前提だが保険）
        if (cp < 0x20 || (cp >= 0x7F && cp < 0xA0))
        {
            return 0;
        }

        // 結合文字（幅0）
        if (IsZeroWidth(cp))
        {
            return 0;
        }

        return IsWide(cp) ? 2 : 1;
    }

    private static bool IsZeroWidth(int cp) =>
        cp == 0x200B ||                      // ZERO WIDTH SPACE
        cp == 0x200C || cp == 0x200D ||      // ZWNJ / ZWJ
        cp == 0xFEFF ||                      // ZERO WIDTH NO-BREAK SPACE
        (cp >= 0x0300 && cp <= 0x036F) ||    // Combining Diacritical Marks
        (cp >= 0x1AB0 && cp <= 0x1AFF) ||
        (cp >= 0x1DC0 && cp <= 0x1DFF) ||
        (cp >= 0x20D0 && cp <= 0x20FF) ||    // Combining Marks for Symbols
        (cp >= 0xFE00 && cp <= 0xFE0F) ||    // Variation Selectors
        (cp >= 0xFE20 && cp <= 0xFE2F) ||    // Combining Half Marks
        (cp >= 0xE0100 && cp <= 0xE01EF);    // Variation Selectors Supplement

    /// <summary>East Asian Width の W/F（全角）レンジ判定。Unicode 15 相当の主要レンジ。</summary>
    private static bool IsWide(int cp) =>
        (cp >= 0x1100 && cp <= 0x115F) ||    // Hangul Jamo（初声）
        cp == 0x2329 || cp == 0x232A ||      // 山括弧
        (cp >= 0x2E80 && cp <= 0x303E) ||    // CJK部首補助〜CJK記号（〼まで）
        (cp >= 0x3041 && cp <= 0x33FF) ||    // ひらがな・カタカナ・CJK互換
        (cp >= 0x3400 && cp <= 0x4DBF) ||    // CJK拡張A
        (cp >= 0x4E00 && cp <= 0x9FFF) ||    // CJK統合漢字
        (cp >= 0xA000 && cp <= 0xA4CF) ||    // イ族文字
        (cp >= 0xA960 && cp <= 0xA97F) ||    // Hangul Jamo拡張A
        (cp >= 0xAC00 && cp <= 0xD7A3) ||    // ハングル音節
        (cp >= 0xF900 && cp <= 0xFAFF) ||    // CJK互換漢字
        (cp >= 0xFE10 && cp <= 0xFE19) ||    // 縦書き形
        (cp >= 0xFE30 && cp <= 0xFE6F) ||    // CJK互換形・小字形
        (cp >= 0xFF00 && cp <= 0xFF60) ||    // 全角英数・記号
        (cp >= 0xFFE0 && cp <= 0xFFE6) ||    // 全角記号
        (cp >= 0x1B000 && cp <= 0x1B2FF) ||  // かな補助
        (cp >= 0x1F300 && cp <= 0x1F64F) ||  // 絵文字（Misc Symbols & Pictographs / Emoticons）
        (cp >= 0x1F900 && cp <= 0x1F9FF) ||  // 絵文字（Supplemental Symbols）
        (cp >= 0x1FA70 && cp <= 0x1FAFF) ||  // 絵文字（Extended-A）
        (cp >= 0x20000 && cp <= 0x2FFFD) ||  // CJK拡張B〜
        (cp >= 0x30000 && cp <= 0x3FFFD);
}
