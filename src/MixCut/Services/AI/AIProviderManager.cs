using Microsoft.Extensions.Logging;
using MixCut.Utilities;

namespace MixCut.Services.AI;

/// <summary>
/// AI 提供商工厂。对应 macOS 版 AIProviderManager。
/// 根据当前设置创建客户端；活跃提供商无 Key 时自动回退到有 Key 的提供商。
/// 注册为单例服务。
/// </summary>
public sealed class AIProviderManager
{
    private readonly AppSettings _settings;
    private readonly ILoggerFactory _loggerFactory;

    public AIProviderManager(AppSettings settings, ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _loggerFactory = loggerFactory;
    }

    /// <summary>根据当前设置创建 AI 提供商实例。</summary>
    public IAiProvider CurrentProvider()
    {
        var logger = _loggerFactory.CreateLogger<AIProviderManager>();
        var active = _settings.ActiveProvider;

        if (_settings.HasApiKey(active))
        {
            logger.LogInformation("使用活跃提供商: {Provider}", active.DisplayName());
            return CreateProvider(active);
        }

        foreach (var fallback in Enum.GetValues<AIProviderType>())
        {
            if (fallback != active && _settings.HasApiKey(fallback))
            {
                logger.LogInformation("活跃提供商 {Active} 无 Key，回退到 {Fallback}",
                    active.DisplayName(), fallback.DisplayName());
                return CreateProvider(fallback);
            }
        }

        logger.LogError("所有提供商均未配置 API Key！");
        return CreateProvider(active);
    }

    /// <summary>创建指定类型的 AI 提供商。</summary>
    public IAiProvider CreateProvider(AIProviderType type)
    {
        var model = _settings.SelectedModel(type);
        var apiKey = _settings.GetApiKey(type) ?? string.Empty;
        var baseUrl = ResolveBaseUrl(type);
        var logger = _loggerFactory.CreateLogger<OpenAICompatibleClient>();
        logger.LogInformation("创建 AI 客户端: provider={Provider}, model={Model}, hasKey={HasKey}",
            type.DisplayName(), model, !string.IsNullOrEmpty(apiKey));
        return new OpenAICompatibleClient(type, apiKey, model, baseUrl, logger);
    }

    private string ResolveBaseUrl(AIProviderType type) => type switch
    {
        AIProviderType.Qwen => "https://dashscope.aliyuncs.com/compatible-mode/v1",
        AIProviderType.Minimax => "https://api.minimax.chat/v1",
        AIProviderType.Deepseek => "https://api.deepseek.com/v1",
        AIProviderType.Claude => "https://api.anthropic.com/v1",
        AIProviderType.ClaudeRelay => _settings.RelayBaseUrl.Trim().Trim('/'),
        AIProviderType.Custom => string.IsNullOrEmpty(_settings.CustomBaseUrl.Trim())
            ? "https://api.openai.com/v1"
            : _settings.CustomBaseUrl.Trim().Trim('/'),
        _ => string.Empty,
    };
}
