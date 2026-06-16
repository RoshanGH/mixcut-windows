namespace MixCut.Services.AI;

/// <summary>AI 提供商错误种类。对应 macOS 版 AIProviderError。</summary>
public enum AIProviderErrorKind
{
    ApiKeyNotConfigured,
    RequestFailed,
    InvalidResponse,
    RateLimited,
    JsonParsingFailed,
    ClientError,
    InsufficientBalance,
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

    /// <summary>4xx 客户端错误（模型名 / 接口地址 / 参数配置错），不可重试 —— 重试也是同样结果，
    /// 应立即提示用户检查配置，而非误导成「网络问题」干等指数退避。</summary>
    public static AIProviderException ClientError(int statusCode, string detail) =>
        new(AIProviderErrorKind.ClientError,
            $"AI 请求被拒绝（HTTP {statusCode}），请检查「设置」里的模型名称、接口地址是否正确。（{detail}）");

    /// <summary>HTTP 402 Payment Required —— AI 服务商账户余额不足（如 DeepSeek「Insufficient Balance」）。
    /// 协议含义就是「需要付费」，绝不能归类成「网络问题」误导用户去查 WiFi / 切 VPN / 重启路由器，
    /// 真正的下一步是去服务商后台充值。不可重试。</summary>
    public static AIProviderException InsufficientBalance() =>
        new(AIProviderErrorKind.InsufficientBalance,
            "AI 服务账户余额不足，请前往你的 AI 服务商（如 DeepSeek / OpenAI）后台充值后重试。");

    public static AIProviderException InvalidResponse(string detail) =>
        new(AIProviderErrorKind.InvalidResponse,
            $"AI 返回了无法识别的内容，请重试。（{detail}）");

    public static AIProviderException RateLimited() =>
        new(AIProviderErrorKind.RateLimited, "AI 请求过于频繁，请等待 1 分钟后重试");

    public static AIProviderException JsonParsingFailed(string detail) =>
        new(AIProviderErrorKind.JsonParsingFailed,
            $"AI 返回的数据格式异常，请重试。（{detail}）");
}
