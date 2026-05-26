using System.Text.Json;

namespace MixCut.Utilities;

/// <summary>
/// JSON 文本列读写助手。对应 macOS 版 SwiftData 的「<c>Data?</c> 字段 + 计算属性」模式。
/// 解码失败时返回空集合（不抛异常），与 macOS 版 <c>try?</c> 行为一致 —— 避免缓存数据损坏导致崩溃。
/// </summary>
public static class JsonColumn
{
    public static IReadOnlyList<T> Read<T>(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return Array.Empty<T>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();
        }
        catch (JsonException)
        {
            return Array.Empty<T>();
        }
    }

    public static string Write<T>(IEnumerable<T> value) => JsonSerializer.Serialize(value);
}
