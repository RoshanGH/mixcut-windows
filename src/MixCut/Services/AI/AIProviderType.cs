namespace MixCut.Services.AI;

/// <summary>支持的 AI 提供商。对应 macOS 版 AIProviderType。</summary>
public enum AIProviderType
{
    Qwen,
    Minimax,
    Deepseek,
    Claude,
    /// <summary>国内转发网关（OpenAI 兼容协议，二级再选 Claude/Gemini/OpenAI）。</summary>
    ClaudeRelay,
    Custom,
}

/// <summary>国内转发网关支持的二级平台。对应 macOS 版 RelayPlatform。</summary>
public enum RelayPlatform
{
    Claude,
    Gemini,
    OpenAI,
}

/// <summary>AI 提供商元数据目录：持久化键、显示名、模型列表等。</summary>
public static class AIProviderCatalog
{
    /// <summary>持久化键（与 macOS 版 rawValue 保持一致，便于配置迁移）。</summary>
    public static string Key(this AIProviderType t) => t switch
    {
        AIProviderType.Qwen => "qwen",
        AIProviderType.Minimax => "minimax",
        AIProviderType.Deepseek => "deepseek",
        AIProviderType.Claude => "claude",
        AIProviderType.ClaudeRelay => "claude_relay",
        AIProviderType.Custom => "custom",
        _ => "qwen",
    };

    public static AIProviderType ProviderFromKey(string? key) => key switch
    {
        "qwen" => AIProviderType.Qwen,
        "minimax" => AIProviderType.Minimax,
        "deepseek" => AIProviderType.Deepseek,
        "claude" => AIProviderType.Claude,
        "claude_relay" => AIProviderType.ClaudeRelay,
        "custom" => AIProviderType.Custom,
        _ => AIProviderType.Qwen,
    };

    public static string DisplayName(this AIProviderType t) => t switch
    {
        AIProviderType.Qwen => "千问",
        AIProviderType.Minimax => "MiniMax",
        AIProviderType.Deepseek => "DeepSeek",
        AIProviderType.Claude => "Claude",
        AIProviderType.ClaudeRelay => "国内转发网关",
        AIProviderType.Custom => "自定义",
        _ => t.ToString(),
    };

    /// <summary>是否使用 Claude 原生 API（非 OpenAI 兼容格式）。</summary>
    public static bool IsClaudeNative(this AIProviderType t) => t == AIProviderType.Claude;

    /// <summary>固定模型列表（claudeRelay/custom 为动态，返回空）。</summary>
    public static IReadOnlyList<string> StaticModels(this AIProviderType t) => t switch
    {
        AIProviderType.Qwen => new[]
        {
            "qwen3-max", "qwen-max-latest", "qwen3-coder-plus",
            "qwen-plus-latest", "qwen-flash", "qwen-turbo-latest",
        },
        AIProviderType.Minimax => new[]
        {
            "MiniMax-M2.7", "MiniMax-M2.5", "MiniMax-M2.1",
            "MiniMax-M2.7-highspeed", "MiniMax-M2.5-highspeed",
        },
        AIProviderType.Deepseek => new[]
        {
            "deepseek-v4-pro", "deepseek-v4-flash", "deepseek-chat", "deepseek-reasoner",
        },
        AIProviderType.Claude => new[]
        {
            "claude-opus-4-7", "claude-sonnet-4-6", "claude-haiku-4-5-20251001",
            "claude-opus-4-6", "claude-sonnet-4-5-20250929",
        },
        _ => Array.Empty<string>(),
    };

    public static string StaticDefaultModel(this AIProviderType t) => t switch
    {
        AIProviderType.Qwen => "qwen-plus-latest",
        AIProviderType.Minimax => "MiniMax-M2.5",
        AIProviderType.Deepseek => "deepseek-v4-flash",
        AIProviderType.Claude => "claude-sonnet-4-6",
        _ => "custom-model",
    };

    // ---- RelayPlatform ----

    public static string Key(this RelayPlatform p) => p switch
    {
        RelayPlatform.Claude => "claude",
        RelayPlatform.Gemini => "gemini",
        RelayPlatform.OpenAI => "openai",
        _ => "claude",
    };

    public static RelayPlatform RelayFromKey(string? key) => key switch
    {
        "gemini" => RelayPlatform.Gemini,
        "openai" => RelayPlatform.OpenAI,
        _ => RelayPlatform.Claude,
    };

    public static string DisplayName(this RelayPlatform p) => p switch
    {
        RelayPlatform.Claude => "Claude (Anthropic)",
        RelayPlatform.Gemini => "Gemini (Google)",
        RelayPlatform.OpenAI => "OpenAI / GPT",
        _ => p.ToString(),
    };

    public static IReadOnlyList<string> Models(this RelayPlatform p) => p switch
    {
        RelayPlatform.Claude => new[]
        {
            "claude-opus-4-7", "claude-sonnet-4-6", "claude-haiku-4-5-20251001",
            "claude-opus-4-6", "claude-sonnet-4-5-20250929",
        },
        RelayPlatform.Gemini => new[]
        {
            "gemini-3.1-pro-preview", "gemini-2.5-pro", "gemini-3-flash-preview",
            "gemini-2.5-flash", "gemini-3.1-flash-lite",
        },
        RelayPlatform.OpenAI => new[]
        {
            "gpt-5.5", "gpt-5.4-mini", "gpt-5-mini", "o3", "gpt-5-nano", "gpt-4o",
        },
        _ => Array.Empty<string>(),
    };

    public static string DefaultModel(this RelayPlatform p) => p switch
    {
        RelayPlatform.Claude => "claude-sonnet-4-6",
        RelayPlatform.Gemini => "gemini-2.5-flash",
        RelayPlatform.OpenAI => "gpt-5.4-mini",
        _ => "claude-sonnet-4-6",
    };

    /// <summary>
    /// 模型显示名（带分级/推荐标注），对齐 macOS 版 modelDisplayName。
    /// 未识别的模型 ID 原样返回。
    /// </summary>
    public static string ModelDisplayName(string model) => model switch
    {
        // 千问
        "qwen3-max" => "Qwen3 Max (旗舰)",
        "qwen-max-latest" => "Qwen Max (稳定旗舰)",
        "qwen3-coder-plus" => "Qwen3 Coder Plus (代码强化)",
        "qwen-plus-latest" => "Qwen Plus (主力 · 推荐)",
        "qwen-flash" => "Qwen Flash (快/高性价比)",
        "qwen-turbo-latest" => "Qwen Turbo (极速)",
        // MiniMax
        "MiniMax-M2.7" => "MiniMax M2.7 (最新旗舰)",
        "MiniMax-M2.5" => "MiniMax M2.5 (稳定主力 · 推荐)",
        "MiniMax-M2.1" => "MiniMax M2.1 (编程强化)",
        "MiniMax-M2.7-highspeed" => "MiniMax M2.7 高速版",
        "MiniMax-M2.5-highspeed" => "MiniMax M2.5 高速版",
        // DeepSeek
        "deepseek-v4-pro" => "DeepSeek V4 Pro (旗舰 · 1M ctx)",
        "deepseek-v4-flash" => "DeepSeek V4 Flash (推荐 · 1M ctx)",
        "deepseek-chat" => "DeepSeek Chat (兼容别名 · 2026-07 废弃)",
        "deepseek-reasoner" => "DeepSeek Reasoner (兼容别名 · 2026-07 废弃)",
        // Claude
        "claude-opus-4-7" => "Claude Opus 4.7 (最强)",
        "claude-sonnet-4-6" => "Claude Sonnet 4.6 (推荐)",
        "claude-haiku-4-5-20251001" => "Claude Haiku 4.5 (快速)",
        "claude-opus-4-6" => "Claude Opus 4.6 (上一代旗舰)",
        "claude-sonnet-4-5-20250929" => "Claude Sonnet 4.5 (上一代主力)",
        // Gemini
        "gemini-3.1-pro-preview" => "Gemini 3.1 Pro Preview (最强)",
        "gemini-2.5-pro" => "Gemini 2.5 Pro (稳定旗舰)",
        "gemini-3-flash-preview" => "Gemini 3 Flash Preview",
        "gemini-2.5-flash" => "Gemini 2.5 Flash (推荐)",
        "gemini-3.1-flash-lite" => "Gemini 3.1 Flash Lite",
        // OpenAI
        "gpt-5.5" => "GPT-5.5 (旗舰)",
        "gpt-5.4-mini" => "GPT-5.4 Mini (推荐)",
        "gpt-5-mini" => "GPT-5 Mini",
        "o3" => "OpenAI o3 (推理)",
        "gpt-5-nano" => "GPT-5 Nano",
        "gpt-4o" => "GPT-4o (上一代旗舰)",
        _ => model,
    };
}
