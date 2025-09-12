using System;
using System.Text.RegularExpressions;

class TestClaudeCodePattern
{
    static void Main(string[] args)
    {
        // テスト用のClaude Code出力サンプル
        var testOutputs = new[]
        {
            // 旧形式
            "· Concocting… (7s · ↓ 100 tokens · esc to interrupt)",
            "✽ Accomplishing… (25s · ⚒ 214 tokens · esc to interrupt)",
            "· Determining… (8s · ↑ 26 tokens · esc to interrupt)",
            // 新形式
            "✶ Program.csでサービスを登録中… (esc to interrupt · ctrl+t to show todos)",
            "✶ Spellbinding… (esc to interrupt)",
            "✽ 設定ファイルを読み込み中… (esc to interrupt · ctrl+t to show todos)",
            "✹ Processing request… (esc to interrupt)",
            "* Updating the detection pattern for new format… (esc to interrupt · ctrl+t to show todos)",
            // 中断
            "[Request interrupted by user]",
            // マッチしないもの
            "Some other output that should not match",
            // 複数行のテストケース（マッチしないはず）
            "✶ 何か他の行\nの内容 (esc to interrupt)",
            "· Testing\nmultiline (7s · ↓ 100 tokens · esc to interrupt)"
        };

        // パターン（ClaudeCodeAnalyzerと同じ）
        var patterns = new[]
        {
            // 旧形式（単一行のみマッチ）
            @"[·✽]\s*(?:[^\r\n()]*?ing[^\r\n()]*)\s*\((\d+)s\s*·\s*([↑↓⚒])\s*([\d.]+[kK]?)\s*tokens?\s*·\s*esc to interrupt\)",
            // 新形式（単一行のみマッチ）
            @"[✶✽✻✼✴✵✷✸✹·⋆*]\s*([^\r\n()]+?)\s*\(esc to interrupt(?:\s*·\s*ctrl\+t to show todos)?\)",
            // 中断
            @"\[Request interrupted by user\]"
        };

        foreach (var output in testOutputs)
        {
            Console.WriteLine($"Testing: {output}");
            
            foreach (var pattern in patterns)
            {
                var regex = new Regex(pattern);
                var matches = regex.Matches(output);
                
                foreach (Match match in matches)
                {
                    if (pattern.Contains("Request interrupted"))
                    {
                        Console.WriteLine("  → [Claude Code Status] 処理が中断されました");
                    }
                    else if (pattern.Contains("esc to interrupt(?:"))
                    {
                        // 新形式
                        var processingText = match.Groups[1].Value.Trim();
                        Console.WriteLine($"  → [Claude Code Status] 処理中: {processingText}");
                    }
                    else if (match.Groups.Count >= 4 && pattern.Contains("tokens"))
                    {
                        // 旧形式
                        var action = match.Groups[1].Value.Trim();
                        var seconds = match.Groups[2].Value;
                        var direction = match.Groups[3].Value;
                        var tokens = match.Groups[4].Value;
                        
                        var directionText = direction switch
                        {
                            "↑" => "送信",
                            "↓" => "受信", 
                            "⚒" => "処理中",
                            _ => direction
                        };
                        
                        Console.WriteLine($"  → [Claude Code Status] {action} - {seconds}秒経過 · {directionText} {tokens}トークン");
                    }
                }
            }
            Console.WriteLine();
        }
    }
}