using System.IO;

namespace MixCut.Services.AI;

/// <summary>
/// Prompt 模板加载器。对应 macOS 版 PromptLoader。
/// 从应用目录 <c>Resources\Prompts\&lt;name&gt;.md</c> 加载。注册为单例服务。
/// </summary>
public sealed class PromptLoader
{
    private static string PromptsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Resources", "Prompts");

    /// <summary>加载指定名称的 prompt 模板（不含扩展名）；不存在时返回 null。</summary>
    public string? LoadPrompt(string name)
    {
        var path = Path.Combine(PromptsDirectory, name + ".md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
