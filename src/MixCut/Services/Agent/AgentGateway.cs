using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MixCut.Data;
using MixCut.Services.Export;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;
using MixCut.ViewModels;

namespace MixCut.Services.Agent;

/// <summary>
/// Agent 接入的装配与生命周期管理（issue #23，对齐 macOS AgentGateway）：
/// 持有 MCP server 与工具处理器；开关/端口变更即重启 listener；
/// 工具调用统一编组到 UI 线程执行（单写者，与 UI 共用同一写路径）。
/// </summary>
public sealed class AgentGateway
{
    public const int DefaultPort = 8787;

    /// <summary>Agent 写库后广播「数据已变更，全量重载」（MainWindow 订阅 → RefreshAfterProjectChange）。</summary>
    public static event Action? UiReloadRequested;

    private readonly IServiceProvider _services;
    private readonly AppSettings _settings;
    private readonly ILogger<AgentGateway> _logger;
    private readonly AgentJobRegistry _jobs = new();
    private McpToolHandlers? _handlers;
    private McpServer? _server;

    public AgentGateway(IServiceProvider services, AppSettings settings, ILogger<AgentGateway> logger)
    {
        _services = services;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>注册地址（设置页展示与片段生成用）。</summary>
    public static string EndpointUrl(int port) => $"http://127.0.0.1:{port}/mcp";

    public static void NotifyUiReload() => UiReloadRequested?.Invoke();

    /// <summary>启动期装配：构建工具处理器并按当前设置启动 server。</summary>
    public void Configure()
    {
        // 方案生成用独立 headless SchemeViewModel（对齐 mac）：避免 Agent 生成时
        // 抢占 UI「混剪方案」页正在展示的 VM 状态（Schemes 集合 / IsGenerating）。
        var headlessSchemeVM = ActivatorUtilities.CreateInstance<SchemeViewModel>(_services);
        _handlers = new McpToolHandlers(
            _services.GetRequiredService<IDbContextFactory<MixCutDbContext>>(),
            _services.GetRequiredService<ImportViewModel>(),
            _services.GetRequiredService<DubbingViewModel>(),
            headlessSchemeVM,
            _services.GetRequiredService<ProjectViewModel>(),
            _services.GetRequiredService<ExportService>(),
            _services.GetRequiredService<BatchSegmentExportService>(),
            _services.GetRequiredService<FFmpegRunner>(),
            _settings,
            _jobs,
            _logger);
        RestartFromSettings();
    }

    /// <summary>按当前设置重启 server（开关/端口变更后调用）。UI 线程调用。</summary>
    public void RestartFromSettings()
    {
        if (_handlers is null)
        {
            return;
        }
        _server?.Stop();
        _server = null;
        if (!_settings.AgentServerEnabled)
        {
            return;
        }
        var port = _settings.AgentServerPort;
        var server = new McpServer(port, DispatchAsync, _logger);
        try
        {
            server.Start();
            _server = server;
        }
        catch (SocketException)
        {
            // 端口被占用：不崩溃、人话提示（§1.4 区块 1 文案）
            _logger.LogWarning("[MCP] 端口 {Port} 监听失败（可能被占用）", port);
            Views.Components.ToastService.Show(
                $"Agent 服务启动失败：端口 {port} 可能被占用，可在设置中修改",
                Views.Components.ToastStyle.Error);
        }
    }

    public void Stop()
    {
        _server?.Stop();
        _server = null;
    }

    /// <summary>
    /// JSON-RPC 分发：解析 → 路由 → 编码。返回 null 表示 notification 无需应答（HTTP 202）。
    /// tools/call 编组到 UI 线程串行执行；协议层方法（initialize/ping/list）无状态可在网络线程直接答。
    /// </summary>
    private async Task<byte[]?> DispatchAsync(byte[] body)
    {
        switch (McpProtocol.Parse(body))
        {
            case McpIncoming.Initialize init:
                var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";
                return McpProtocol.InitializeResponse(init.Id, "mixcut", version, AgentToolCatalog.ServerInstructions);
            case McpIncoming.Notification:
                return null;
            case McpIncoming.Ping ping:
                return McpProtocol.PingResponse(ping.Id);
            case McpIncoming.ToolsList list:
                return McpProtocol.ToolsListResponse(list.Id, AgentToolCatalog.ToolsArray);
            case McpIncoming.ToolsCall call:
                var handlers = _handlers;
                if (handlers is null)
                {
                    return McpProtocol.ErrorResponse(call.Id, -32603, "服务尚未就绪，请稍后重试");
                }
                var dispatcher = Application.Current?.Dispatcher;
                McpToolHandlers.Outcome outcome;
                if (dispatcher is null)
                {
                    outcome = await handlers.CallAsync(call.Name, call.ArgumentsJson);
                }
                else
                {
                    outcome = await await dispatcher.InvokeAsync(() => handlers.CallAsync(call.Name, call.ArgumentsJson));
                }
                return McpProtocol.ToolCallResponse(call.Id, outcome.Json, outcome.IsError);
            case McpIncoming.Invalid invalid:
                return McpProtocol.ErrorResponse(invalid.Id, invalid.Code, invalid.Message);
            default:
                return McpProtocol.ErrorResponse(null, -32600, "无效的 JSON-RPC 请求");
        }
    }
}
