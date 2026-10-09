using System.Text.Json;

namespace TerminalHub.Mcp
{
    /// <summary>
    /// MCP ツール呼び出しの引数を、ツールの入力スキーマ（JSON Schema）と突き合わせて
    /// 「必須引数が欠けている」「存在しない引数名を渡している」を呼び出し側に分かる言葉で返す。
    ///
    /// 背景: SDK（ModelContextProtocol 2.1）は必須引数が欠けると引数束縛の段階で
    /// ArgumentException を投げ、クライアントには "An error occurred invoking 'send_to_session'."
    /// という汎用文しか返さない。呼び出し側の LLM は何が悪いか分からず、同じ誤りで再試行を
    /// 繰り返す（2026-10-09 に targetSessionId を sessionId と書いて 6 連続失敗した実害あり）。
    /// ツール本体に届く前にここで検査し、引数名を列挙したエラーを返して 1 回で立ち直れるようにする。
    ///
    /// 純ロジック（System.Text.Json のみ）にして、テストプロジェクトからリンクして検証できるようにしている。
    /// </summary>
    public static class McpToolArgumentCheck
    {
        /// <summary>
        /// 引数の問題を説明する文を返す。問題が無ければ null。
        /// </summary>
        /// <param name="toolName">ツール名（メッセージ用）</param>
        /// <param name="inputSchema">ツールの入力スキーマ（<c>properties</c> と <c>required</c> を見る）</param>
        /// <param name="providedArgumentNames">呼び出しで渡された引数名</param>
        public static string? Describe(
            string toolName,
            JsonElement inputSchema,
            IEnumerable<string>? providedArgumentNames)
        {
            var provided = new HashSet<string>(providedArgumentNames ?? Array.Empty<string>(), StringComparer.Ordinal);
            var known = ReadPropertyNames(inputSchema);
            var required = ReadRequiredNames(inputSchema);

            // スキーマが読めない（properties 無し）ときは判断材料が無いので何もしない
            if (known.Count == 0)
            {
                return null;
            }

            var missing = required.Where(r => !provided.Contains(r)).ToList();
            if (missing.Count == 0)
            {
                // 余計な引数だけなら SDK が無視して動くので通す
                return null;
            }

            var unknown = provided.Where(p => !known.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();

            var sb = new System.Text.StringBuilder();
            sb.Append(toolName).Append(" の引数が誤っています。必須引数 ")
              .Append(string.Join(", ", missing.Select(m => $"'{m}'")))
              .Append(" がありません。");
            if (unknown.Count > 0)
            {
                sb.Append(string.Join(", ", unknown.Select(u => $"'{u}'")))
                  .Append(" はこのツールの引数ではありません。");
            }
            sb.Append("このツールの引数: ")
              .Append(string.Join(", ", known.Select(k => required.Contains(k) ? $"{k}(必須)" : k)))
              .Append("。引数名を直して呼び直してください。");
            return sb.ToString();
        }

        private static List<string> ReadPropertyNames(JsonElement schema)
        {
            var names = new List<string>();
            if (schema.ValueKind == JsonValueKind.Object
                && schema.TryGetProperty("properties", out var props)
                && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in props.EnumerateObject())
                {
                    names.Add(p.Name);
                }
            }
            return names;
        }

        private static HashSet<string> ReadRequiredNames(JsonElement schema)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (schema.ValueKind == JsonValueKind.Object
                && schema.TryGetProperty("required", out var req)
                && req.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in req.EnumerateArray())
                {
                    if (r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } s)
                    {
                        names.Add(s);
                    }
                }
            }
            return names;
        }
    }
}
