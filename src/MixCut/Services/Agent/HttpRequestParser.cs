using System.Text;

namespace MixCut.Services.Agent;

/// <summary>解析出的一条 HTTP 请求（极简 HTTP/1.1，只为本机 MCP 传输服务）。</summary>
public sealed record ParsedHttpRequest(string Method, string Path, byte[] Body, int ConsumedBytes);

/// <summary>
/// 极简 HTTP/1.1 请求解析器：从字节缓冲里切出一条完整请求（头 + Content-Length 定长 body）。
/// 头名大小写不敏感；不支持 chunked（MCP Streamable HTTP 客户端都发定长 body）。
/// 对齐 macOS 版 HTTPRequestParser 行为。
/// </summary>
public static class HttpRequestParser
{
    /// <summary>缓冲里凑不齐完整请求时返回 null（调用方继续收字节）。</summary>
    public static ParsedHttpRequest? Parse(byte[] buffer, int length)
    {
        var headerEnd = IndexOfHeaderEnd(buffer, length);
        if (headerEnd < 0)
        {
            return null;
        }

        var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        if (lines.Length == 0)
        {
            return null;
        }
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            return null;
        }
        var method = requestLine[0];
        var path = requestLine[1];
        // 容忍 querystring（?xxx）与绝对形式，仅取 path 部分
        var qIdx = path.IndexOf('?');
        if (qIdx >= 0)
        {
            path = path[..qIdx];
        }

        var contentLength = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            var name = lines[i][..colon].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(lines[i][(colon + 1)..].Trim(), out contentLength);
            }
        }

        var bodyStart = headerEnd + 4;
        if (length < bodyStart + contentLength)
        {
            return null;   // body 还没收全
        }

        var body = new byte[contentLength];
        Array.Copy(buffer, bodyStart, body, 0, contentLength);
        return new ParsedHttpRequest(method, path, body, bodyStart + contentLength);
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        // 找 \r\n\r\n
        for (var i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n'
                && buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
            {
                return i;
            }
        }
        return -1;
    }
}
