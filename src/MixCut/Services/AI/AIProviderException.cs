namespace MixCut.Services.AI;

/// <summary>AI 提供商错误种类。对应 macOS 版 AIProviderError。</summary>
public enum AIProviderErrorKind
{
    ApiKeyNotConfigured,
    RequestFailed,
    InvalidResponse,
    RateLimited,
    JsonParsingFailed,
}

/// <summary>AI 提供商调用异常，携带面向用户的中文提示。</summary>
public sealed class AIProviderException : Exception
{
    public AIProviderErrorKind Kind { get; }

    public AIProviderException(AIProviderErrorKind kind, string message) : base(message)
    {
        Kind = kind;
    }

    public static AIProviderException ApiKeyNotConfigured(AIProviderType provider) =>
        new(AIProviderErrorKind.ApiKeyNotConfigured,
            $"{provider.DisplayName()} API Key 未配置，请在设置中添加");

    public static AIProviderException RequestFailed(string detail) =>
        new(AIProviderErrorKind.RequestFailed,
            $"AI 服务连接失败，请检查网络后重试。（{detail}）");

    public static AIProviderException InvalidResponse(string detail) =>
        new(AIProviderErrorKind.InvalidResponse,
            $"AI 返回了无法识别的内容，请重试。（{detail}）");

    public static AIProviderException RateLimited() =>
        new(AIProviderErrorKind.RateLimited, "AI 请求过于频繁，请等待 1 分钟后重试");

    public static AIProviderException JsonParsingFailed(string detail) =>
        new(AIProviderErrorKind.JsonParsingFailed,
            $"AI 返回的数据格式异常，请重试。（{detail}）");
}
