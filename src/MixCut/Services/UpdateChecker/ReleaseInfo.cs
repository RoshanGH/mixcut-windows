namespace MixCut.Services.UpdateChecker;

/// <summary>
/// 远端最新 release 元信息。Source 标识探测成功的渠道，HtmlUrl 是用户当前网络环境能打开的页面。
/// </summary>
public sealed class ReleaseInfo
{
    public string Tag { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty; // "GitHub" 或 "Gitee"
}
