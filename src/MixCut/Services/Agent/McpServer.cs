using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MixCut.Services.Agent;

/// <summary>
/// 内嵌本地 MCP server 的 HTTP 传输壳（对齐 macOS MCPServer）。
/// 只绑定 127.0.0.1，只接受 POST /mcp；JSON-RPC 语义全部委托给注入的 handler。
/// 支持 keep-alive（同连接串行多请求）；单连接缓冲上限 4MB，超限断开。
/// </summary>
public sealed class McpServer
{
    private const int MaxBufferBytes = 4 * 1024 * 1024;

    private readonly int _port;
    private readonly Func<byte[], Task<byte[]?>> _handler;
    private readonly ILogger _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public McpServer(int port, Func<byte[], Task<byte[]?>> handler, ILogger logger)
    {
        _port = port;
        _handler = handler;
        _logger = logger;
    }

    /// <summary>启动监听。端口被占用等失败会抛 SocketException，由调用方翻译成人话。</summary>
    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }
        // 只监听回环地址：外部机器不可达（硬性，绝不 0.0.0.0）
        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        _listener = listener;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(listener, _cts.Token);
        _logger.LogInformation("[MCP] server 已监听 127.0.0.1:{Port}", _port);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // 停止监听失败不影响退出
        }
        _listener = null;
        _logger.LogInformation("[MCP] server 已停止");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;   // listener 已停止
            }
            _ = ServeAsync(client, token);
        }
    }

    /// <summary>单连接处理：keep-alive 串行多请求。</summary>
    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[64 * 1024];
            var data = new MemoryStream();
            while (!token.IsCancellationRequested)
            {
                var raw = data.GetBuffer();
                var request = HttpRequestParser.Parse(raw, (int)data.Length);
                if (request is not null)
                {
                    // 消费掉本条请求的字节，剩余留给下一条（pipeline 容错）
                    var remaining = (int)data.Length - request.ConsumedBytes;
                    var rest = new byte[remaining];
                    Array.Copy(raw, request.ConsumedBytes, rest, 0, remaining);
                    data.SetLength(0);
                    data.Write(rest);

                    var response = await RouteAsync(request);
                    await stream.WriteAsync(response, token);
                    continue;
                }

                var read = await stream.ReadAsync(buffer, token);
                if (read <= 0)
                {
                    break;
                }
                data.Write(buffer, 0, read);
                if (data.Length > MaxBufferBytes)
                {
                    break;   // 防御：异常大请求直接断开
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // 连接层错误：对端断开等，静默收尾
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MCP] 连接处理异常");
        }
    }

    private async Task<byte[]> RouteAsync(ParsedHttpRequest request)
    {
        if (request.Path != "/mcp")
        {
            return HttpResponse("404 Not Found", Encoding.UTF8.GetBytes("not found"), "text/plain");
        }
        if (request.Method != "POST")
        {
            return HttpResponse("405 Method Not Allowed", Encoding.UTF8.GetBytes("method not allowed"), "text/plain");
        }
        var responseBody = await _handler(request.Body);
        if (responseBody is null)
        {
            // JSON-RPC notification：无应答体
            return HttpResponse("202 Accepted", Array.Empty<byte>(), "application/json");
        }
        return HttpResponse("200 OK", responseBody, "application/json");
    }

    private static byte[] HttpResponse(string status, byte[] body, string contentType)
    {
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: keep-alive\r\n\r\n";
        var headBytes = Encoding.ASCII.GetBytes(head);
        var result = new byte[headBytes.Length + body.Length];
        headBytes.CopyTo(result, 0);
        body.CopyTo(result, headBytes.Length);
        return result;
    }
}
