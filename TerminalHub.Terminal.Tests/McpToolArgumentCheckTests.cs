using System.Text.Json;
using TerminalHub.Mcp;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// MCP ツール呼び出しの引数検査（<see cref="McpToolArgumentCheck"/>）の検証。
/// 必須引数の欠落を「どの引数が無く、何を渡してしまい、正しい引数は何か」まで言う文にし、
/// 余計な引数だけのときや、スキーマが読めないときは黙って通すことを固定する。
/// </summary>
public sealed class McpToolArgumentCheckTests
{
    // send_to_session と同じ形のスキーマ
    private static readonly JsonElement SendSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "targetSessionId": { "type": "string" },
            "message": { "type": "string" },
            "contextId": { "type": ["string", "null"] }
          },
          "required": ["targetSessionId", "message"]
        }
        """).RootElement;

    [Fact]
    public void 必須引数が揃っていれば問題なし()
    {
        var actual = McpToolArgumentCheck.Describe("send_to_session", SendSchema, new[] { "targetSessionId", "message" });
        Assert.Null(actual);
    }

    [Fact]
    public void 引数名を間違えたら欠けた必須引数と誤った名前と正しい引数一覧を言う()
    {
        // 2026-10-09 の実害そのもの: targetSessionId を sessionId と書いた
        var actual = McpToolArgumentCheck.Describe("send_to_session", SendSchema, new[] { "sessionId", "message", "contextId" });

        Assert.NotNull(actual);
        Assert.Contains("必須引数 'targetSessionId' がありません", actual);
        Assert.Contains("'sessionId' はこのツールの引数ではありません", actual);
        Assert.Contains("targetSessionId(必須), message(必須), contextId", actual);
        Assert.StartsWith("send_to_session の引数", actual);
    }

    [Fact]
    public void 複数の必須引数が欠けていれば全部列挙する()
    {
        var actual = McpToolArgumentCheck.Describe("send_to_session", SendSchema, new[] { "text" });

        Assert.NotNull(actual);
        Assert.Contains("'targetSessionId', 'message' がありません", actual);
        Assert.Contains("'text' はこのツールの引数ではありません", actual);
    }

    [Fact]
    public void 余計な引数だけなら通す()
    {
        // SDK は未知の引数を無視して動くので、ここで止めない
        var actual = McpToolArgumentCheck.Describe("send_to_session", SendSchema, new[] { "targetSessionId", "message", "extra" });
        Assert.Null(actual);
    }

    [Fact]
    public void 引数なし呼び出しでも必須の欠落を言う()
    {
        var actual = McpToolArgumentCheck.Describe("send_to_session", SendSchema, null);

        Assert.NotNull(actual);
        Assert.Contains("'targetSessionId', 'message' がありません", actual);
        Assert.DoesNotContain("はこのツールの引数ではありません", actual);
    }

    [Fact]
    public void 必須引数が無いツールは何を渡しても通す()
    {
        var schema = JsonDocument.Parse("""
            { "type": "object", "properties": { "nameContains": { "type": ["string", "null"] } } }
            """).RootElement;

        Assert.Null(McpToolArgumentCheck.Describe("list_sessions", schema, null));
        Assert.Null(McpToolArgumentCheck.Describe("list_sessions", schema, new[] { "typo" }));
    }

    [Fact]
    public void スキーマにpropertiesが無ければ判断せず通す()
    {
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;
        Assert.Null(McpToolArgumentCheck.Describe("x", schema, new[] { "anything" }));
    }
}
