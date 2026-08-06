using System.Text.Json;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// CharWidth（自動生成テーブル）と xterm.js Unicode11Addon の wcwidth 実測ダンプ
/// （Fixtures/u11-widths.json・experiments/CharWidthGen/generate.js が生成）の全点突き合わせ。
/// 幅の意見が xterm と食い違うと切替時の再生でレイアウトが崩れるため、
/// 1コードポイントの不一致も許さない。
/// </summary>
public class CharWidthParityTests
{
    [Fact]
    public void All_codepoints_match_unicode11_addon_dump()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "u11-widths.json");
        Assert.True(File.Exists(path), $"フィクスチャがありません: {path}");

        var ranges = JsonSerializer.Deserialize<int[][]>(File.ReadAllText(path))!;
        Assert.NotEmpty(ranges);

        var mismatches = new List<string>();
        long covered = 0;
        foreach (var r in ranges)
        {
            int start = r[0], end = r[1], expected = r[2];
            for (int cp = start; cp <= end; cp++)
            {
                covered++;
                int actual = CharWidth.GetWidth(cp);
                if (actual != expected && mismatches.Count < 20)
                {
                    mismatches.Add($"U+{cp:X4}: expected={expected} actual={actual}");
                }
            }
        }

        Assert.Equal(0x110000, covered); // ダンプが全コードポイントを覆っていること
        Assert.True(mismatches.Count == 0, "不一致: " + string.Join(", ", mismatches));
    }

    [Theory]
    [InlineData(0x41, 1)]      // A
    [InlineData(0x65E5, 2)]    // 日
    [InlineData(0x2705, 2)]    // ✅ U+2705（Unicode9でWide化。旧手書きテーブルの取りこぼし代表）
    [InlineData(0x2B50, 2)]    // ⭐
    [InlineData(0x1F680, 2)]   // 🚀（Transport: 旧手書きテーブルの取りこぼし代表）
    [InlineData(0x1F7E1, 2)]   // 🟡（Geometric Extended: 同上）
    [InlineData(0x2600, 1)]    // ☀（VS16なしは半角のまま）
    [InlineData(0xFE0F, 0)]    // VS16 自体は幅0（拡幅は VtParser/TerminalGrid が担当）
    [InlineData(0x0301, 0)]    // 結合アクセント
    [InlineData(0x1F3A4, 2)]   // 🎤
    public void Spot_checks(int cp, int expected)
    {
        Assert.Equal(expected, CharWidth.GetWidth(cp));
    }
}
