using System;
using System.Text.RegularExpressions;

class TestClaudeCodePattern
{
    static void Main(string[] args)
    {
        // テスト用のClaude Code出力サンプル
        var testOutputs = new[]
        {
            "· Concocting… (7s · ↓ 100 tokens · esc to interrupt)",
            "✽ Accomplishing… (25s · ⚒ 214 tokens · esc to interrupt)",
            "· Determining… (8s · ↑ 26 tokens · esc to interrupt)",
            "[Request interrupted by user]",
            "Some other output that should not match"
        };

        // パターン（Root.razorと同じ）
        var patterns = new[]
        {
            @"[·✽]\s*(.*?ing[^(]*)\s*\((\d+)s\s*·\s*([↑↓⚒])\s*(\d+)\s*tokens?\s*·\s*esc to interrupt\)",
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
                    else if (match.Groups.Count >= 5)
                    {
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