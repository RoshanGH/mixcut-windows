using System.IO;
using System.Text.Json;

namespace MixCut.Services.Agent;

/// <summary>JSON-RPC 请求 id：MCP 允许数字或字符串两种。</summary>
public readonly struct McpRequestId
{
    private readonly long _number;
    private readonly string? _text;

    public bool IsString => _text is not null;

    private McpRequestId(long number, string? text)
    {
        _number = number;
        _text = text;
    }

    public static McpRequestId FromNumber(long n) => new(n, null);
    public static McpRequestId FromString(string s) => new(0, s);

    public void WriteTo(Utf8JsonWriter writer, string propertyName)
    {
        if (_text is not null)
        {
            writer.WriteString(propertyName, _text);
        }
        else
        {
            writer.WriteNumber(propertyName, _number);
        }
    }
}

/// <summary>解析后的 MCP 入站消息（对齐 macOS MCPIncoming）。</summary>
public abstract record McpIncoming
{
    public sealed record Initialize(McpRequestId Id) : McpIncoming;
    /// <summary>所有 notifications/*（无 id、无需应答）统一归入此 case。</summary>
    public sealed record Notification : McpIncoming;
    public sealed record Ping(McpRequestId Id) : McpIncoming;
    public sealed record ToolsList(McpRequestId Id) : McpIncoming;
    public sealed record ToolsCall(McpRequestId Id, string Name, string ArgumentsJson) : McpIncoming;
    public sealed record Invalid(McpRequestId? Id, int Code, string Message) : McpIncoming;
}

/// <summary>
/// MCP Streamable HTTP 的 JSON-RPC 解析与编码（纯函数，无网络依赖）。
/// 中文一律不转义（UnsafeRelaxedJsonEscaping），Agent 端看到的是可读中文。
/// </summary>
public static class McpProtocol
{
    public const string ProtocolVersion = "2025-06-18";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---- 解析 ----

    public static McpIncoming Parse(byte[] body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return new McpIncoming.Invalid(null, -32700, "JSON 解析失败");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new McpIncoming.Invalid(null, -32700, "JSON 解析失败");
            }
            var root = doc.RootElement;
            var id = ReadRequestId(root);
            if (!root.TryGetProperty("jsonrpc", out var ver) || ver.ValueKind != JsonValueKind.String
                || ver.GetString() != "2.0"
                || !root.TryGetProperty("method", out var methodProp) || methodProp.ValueKind != JsonValueKind.String)
            {
                return new McpIncoming.Invalid(id, -32600, "无效的 JSON-RPC 请求");
            }
            var method = methodProp.GetString()!;
            if (method.StartsWith("notifications/", StringComparison.Ordinal))
            {
                return new McpIncoming.Notification();
            }
            if (id is null)
            {
                return new McpIncoming.Invalid(null, -32600, "无效的 JSON-RPC 请求");
            }
            switch (method)
            {
                case "initialize": return new McpIncoming.Initialize(id.Value);
                case "ping": return new McpIncoming.Ping(id.Value);
                case "tools/list": return new McpIncoming.ToolsList(id.Value);
                case "tools/call":
                    if (!root.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object
                        || !p.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                    {
                        return new McpIncoming.Invalid(id, -32602, "tools/call 缺少 name 参数");
                    }
                    var argsJson = p.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
                        ? args.GetRawText()
                        : "{}";
                    return new McpIncoming.ToolsCall(id.Value, nameProp.GetString()!, argsJson);
                default:
                    return new McpIncoming.Invalid(id, -32601, $"不支持的方法：{method}");
            }
        }
    }

    private static McpRequestId? ReadRequestId(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idProp))
        {
            return null;
        }
        return idProp.ValueKind switch
        {
            JsonValueKind.Number when idProp.TryGetInt64(out var n) => McpRequestId.FromNumber(n),
            JsonValueKind.String => McpRequestId.FromString(idProp.GetString()!),
            _ => null,
        };
    }

    // ---- 编码 ----

    public static byte[] InitializeResponse(McpRequestId id, string serverName, string serverVersion, string? instructions)
    {
        return Envelope(id, writer =>
        {
            writer.WriteString("protocolVersion", ProtocolVersion);
            writer.WritePropertyName("capabilities");
            writer.WriteStartObject();
            writer.WritePropertyName("tools");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("serverInfo");
            writer.WriteStartObject();
            writer.WriteString("name", serverName);
            writer.WriteString("version", serverVersion);
            writer.WriteEndObject();
            // MCP 规范的服务器自我介绍：客户端会把它注入给模型，任何客户端一注册就自动获得
            if (instructions is not null)
            {
                writer.WriteString("instructions", instructions);
            }
        });
    }

    public static byte[] PingResponse(McpRequestId id) => Envelope(id, _ => { });

    /// <summary>tools/list：直接透传权威附件里的 tools 数组（与 macOS 版逐字段一致）。</summary>
    public static byte[] ToolsListResponse(McpRequestId id, JsonElement toolsArray)
    {
        return Envelope(id, writer =>
        {
            writer.WritePropertyName("tools");
            toolsArray.WriteTo(writer);
        });
    }

    public static byte[] ToolCallResponse(McpRequestId id, string resultJson, bool isError)
    {
        return Envelope(id, writer =>
        {
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", resultJson);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteBoolean("isError", isError);
        });
    }

    public static byte[] ErrorResponse(McpRequestId? id, int code, string message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            if (id is { } realId)
            {
                realId.WriteTo(writer, "id");
            }
            else
            {
                writer.WriteNull("id");
            }
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static byte[] Envelope(McpRequestId id, Action<Utf8JsonWriter> writeResult)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            id.WriteTo(writer, "id");
            writer.WritePropertyName("result");
            writer.WriteStartObject();
            writeResult(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}
