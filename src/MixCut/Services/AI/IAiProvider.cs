namespace MixCut.Services.AI;

/// <summary>AI 提供商接口。对应 macOS 版 AIProvider 协议。</summary>
public interface IAiProvider
{
    /// <summary>发送 prompt 并将返回内容反序列化为指定类型。</summary>
    Task<T> GenerateJsonAsync<T>(string prompt, CancellationToken cancellationToken = default);

    /// <summary>发送 prompt 并返回纯文本。</summary>
    Task<string> GenerateTextAsync(string prompt, CancellationToken cancellationToken = default);
}
