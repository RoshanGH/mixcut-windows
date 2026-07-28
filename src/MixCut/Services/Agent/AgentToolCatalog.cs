using System.IO;
using System.Reflection;
using System.Text.Json;

namespace MixCut.Services.Agent;

/// <summary>Agent 工具错误码（工具层结构化错误，区别于 JSON-RPC 协议层错误）。8 个全集。</summary>
public enum AgentToolErrorCode
{
    InvalidArgument,
    ProjectNotFound,
    VideoNotFound,
    JobNotFound,
    FileNotFound,
    JobAlreadyRunning,
    SegmentNotFound,
    SchemeNotFound,
}

public static class AgentToolErrorCodeExtensions
{
    public static string ToCode(this AgentToolErrorCode code) => code switch
    {
        AgentToolErrorCode.InvalidArgument => "INVALID_ARGUMENT",
        AgentToolErrorCode.ProjectNotFound => "PROJECT_NOT_FOUND",
        AgentToolErrorCode.VideoNotFound => "VIDEO_NOT_FOUND",
        AgentToolErrorCode.JobNotFound => "JOB_NOT_FOUND",
        AgentToolErrorCode.FileNotFound => "FILE_NOT_FOUND",
        AgentToolErrorCode.JobAlreadyRunning => "JOB_ALREADY_RUNNING",
        AgentToolErrorCode.SegmentNotFound => "SEGMENT_NOT_FOUND",
        AgentToolErrorCode.SchemeNotFound => "SCHEME_NOT_FOUND",
        _ => "INVALID_ARGUMENT",
    };
}

/// <summary>工具结果/错误的 JSON 文本编码（中文不转义）。</summary>
public static class AgentJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Encode(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value, Options);
        }
        catch
        {
            return """{"code":"INVALID_ARGUMENT","message":"内部错误：结果无法编码为 JSON"}""";
        }
    }

    public static string Error(AgentToolErrorCode code, string message) =>
        Encode(new Dictionary<string, object?> { ["code"] = code.ToCode(), ["message"] = message });
}

/// <summary>设置页「Agent 能调用的功能」清单条目（从权威附件动态解析，不手写死清单）。</summary>
public sealed record AgentToolInfo(string Name, string Description);

/// <summary>
/// Agent 工具目录：加载内嵌的权威附件（tools_list.json / server_instructions.txt），
/// tools/list 响应与 initialize instructions 均逐字下发，保证与 macOS 版一致。
/// </summary>
public static class AgentToolCatalog
{
    private static readonly Lazy<JsonDocument> ToolsDocument = new(LoadToolsDocument);
    private static readonly Lazy<string> Instructions = new(() => LoadText("server_instructions.txt").TrimEnd('\n'));
    private static readonly Lazy<IReadOnlyList<AgentToolInfo>> ToolInfos = new(ParseToolInfos);

    /// <summary>initialize 握手时下发给客户端的服务器说明（逐字来自权威附件）。</summary>
    public static string ServerInstructions => Instructions.Value;

    /// <summary>tools/list 的 tools 数组（权威附件原文，逐字段一致）。</summary>
    public static JsonElement ToolsArray => ToolsDocument.Value.RootElement
        .GetProperty("result").GetProperty("tools");

    /// <summary>工具清单（name + 中文 description），供设置页动态渲染。</summary>
    public static IReadOnlyList<AgentToolInfo> All => ToolInfos.Value;

    private static JsonDocument LoadToolsDocument() => JsonDocument.Parse(LoadText("tools_list.json"));

    private static IReadOnlyList<AgentToolInfo> ParseToolInfos()
    {
        var list = new List<AgentToolInfo>();
        foreach (var tool in ToolsArray.EnumerateArray())
        {
            list.Add(new AgentToolInfo(
                tool.GetProperty("name").GetString() ?? "",
                tool.GetProperty("description").GetString() ?? ""));
        }
        return list;
    }

    private static string LoadText(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"MixCut.Resources.Agent.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"内嵌资源缺失：{resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
