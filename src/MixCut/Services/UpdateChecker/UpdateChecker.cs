using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MixCut.Services.UpdateChecker;

/// <summary>
/// 版本检测服务（GitHub 优先 / Gitee 容灾）。对齐 macOS v0.3.1 UpdateChecker。
///
/// 设计意图：启动时探测当前网络环境能访问的源，ReleaseInfo.HtmlUrl 直接是用户能打开的页面。
/// - GitHub 通 → Source=GitHub，HtmlUrl=GitHub release URL，海外用户场景
/// - GitHub 不通但 Gitee 通 → Source=Gitee，HtmlUrl=Gitee tag URL，国内用户场景
/// - 两个都不通 → 返回 null，banner 不显示（极少数情况，不打扰）
///
/// 注册为单例服务。
/// </summary>
public sealed class UpdateChecker
{
    private const int TimeoutSec = 5;
    private const string GitHubUrl = "https://api.github.com/repos/RoshanGH/mixcut-windows/releases/latest";
    private const string GiteeUrl = "https://gitee.com/api/v5/repos/jinxiushanhehao/mixcut-windows/releases/latest";
    private const string GiteeTagPrefix = "https://gitee.com/jinxiushanhehao/mixcut-windows/releases/tag/";

    // QW-6：单例 HttpClient。原来每次检查都 new HttpClient() 是 .NET 反模式 ——
    // socket TIME_WAIT 累积可能耗尽 ephemeral port。per-request header 用 HttpRequestMessage
    // 设，不碰 DefaultRequestHeaders（共享实例下线程安全 + 避免 header 污染）。
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(TimeoutSec) };

    private readonly ILogger<UpdateChecker> _logger;

    public UpdateChecker(ILogger<UpdateChecker> logger)
    {
        _logger = logger;
    }

    public async Task<ReleaseInfo?> FetchLatestAsync(CancellationToken cancellationToken = default)
    {
        return await TryGitHubAsync(cancellationToken) ?? await TryGiteeAsync(cancellationToken);
    }

    private async Task<ReleaseInfo?> TryGitHubAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSec));
            using var req = new HttpRequestMessage(HttpMethod.Get, GitHubUrl);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("MixCutWindows", "1.0"));
            using var resp = await Http.SendAsync(req, cts.Token);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);
            if (release is null || string.IsNullOrEmpty(release.TagName))
            {
                return null;
            }
            _logger.LogInformation("[UpdateCheck] GitHub OK tag={Tag}", release.TagName);
            return new ReleaseInfo
            {
                Tag = release.TagName,
                Version = StripV(release.TagName),
                HtmlUrl = release.HtmlUrl,
                Source = "GitHub",
            };
        }
        catch (Exception ex)
        {
            _logger.LogInformation("[UpdateCheck] GitHub fail: {Msg}", ex.Message);
            return null;
        }
    }

    private async Task<ReleaseInfo?> TryGiteeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSec));
            var json = await Http.GetStringAsync(GiteeUrl, cts.Token);
            var release = JsonSerializer.Deserialize<GiteeRelease>(json);
            if (release is null || string.IsNullOrEmpty(release.TagName))
            {
                return null;
            }
            _logger.LogInformation("[UpdateCheck] Gitee OK tag={Tag}", release.TagName);
            return new ReleaseInfo
            {
                Tag = release.TagName,
                Version = StripV(release.TagName),
                HtmlUrl = GiteeTagPrefix + release.TagName,
                Source = "Gitee",
            };
        }
        catch (Exception ex)
        {
            _logger.LogInformation("[UpdateCheck] Gitee fail: {Msg}", ex.Message);
            return null;
        }
    }

    /// <summary>语义化版本比较：remote 严格大于 local 返回 true（按 . 拆数字逐位比）。</summary>
    public static bool IsNewer(string remote, string local)
    {
        if (string.IsNullOrWhiteSpace(remote) || string.IsNullOrWhiteSpace(local)) return false;
        int[] Parse(string v) => v.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var r = Parse(remote);
        var l = Parse(local);
        var max = Math.Max(r.Length, l.Length);
        for (var i = 0; i < max; i++)
        {
            var ri = i < r.Length ? r[i] : 0;
            var li = i < l.Length ? l[i] : 0;
            if (ri > li) return true;
            if (ri < li) return false;
        }
        return false;
    }

    private static string StripV(string tag) =>
        tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = string.Empty;
    }
    private sealed class GiteeRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
    }
}
