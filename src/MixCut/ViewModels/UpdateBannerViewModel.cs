using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MixCut.Services.UpdateChecker;
using MixCut.Utilities;

namespace MixCut.ViewModels;

/// <summary>
/// 顶部「发现新版本」banner 的 ViewModel。
/// 启动时静默检查；用户可点「立即下载更新」打开 release 页或点 ✕ 屏蔽该版本提示。
/// </summary>
public partial class UpdateBannerViewModel : ObservableObject
{
    private readonly UpdateChecker _checker;
    private readonly AppSettings _settings;
    private readonly ILogger<UpdateBannerViewModel> _logger;

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private string _currentVersion = "0.0.0";

    [ObservableProperty]
    private ReleaseInfo? _latestInfo;

    /// <summary>Banner 主文案：「v0.6.0 → v0.6.1 (GitHub)」。</summary>
    public string BannerText
    {
        get
        {
            var version = LatestInfo?.Version ?? string.Empty;
            var source = LatestInfo?.Source ?? string.Empty;
            return $"v{CurrentVersion} → v{version} ({source})";
        }
    }

    public UpdateBannerViewModel(
        UpdateChecker checker,
        AppSettings settings,
        ILogger<UpdateBannerViewModel> logger)
    {
        _checker = checker;
        _settings = settings;
        _logger = logger;
        CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>启动时静默检查（fire-and-forget，不阻塞启动）。</summary>
    public async Task CheckSilentlyAsync()
    {
        try
        {
            var info = await _checker.FetchLatestAsync();
            if (info is null) return;
            if (!UpdateChecker.IsNewer(info.Version, CurrentVersion)) return;
            // 用户已点 ✕ 屏蔽过此版本则不再提示
            if (string.Equals(_settings.DismissedUpdateVersion, info.Version, StringComparison.Ordinal)) return;

            LatestInfo = info;
            HasUpdate = true;
            OnPropertyChanged(nameof(BannerText));
            _logger.LogInformation("[UpdateBanner] 显示更新提示: {Local} → {Remote} via {Source}",
                CurrentVersion, info.Version, info.Source);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[UpdateBanner] silent check 失败");
        }
    }

    /// <summary>用户点 ✕ 屏蔽当前版本提示（持久化，下次启动相同版本不再提示）。</summary>
    public void DismissCurrent()
    {
        if (LatestInfo is null) return;
        _settings.DismissedUpdateVersion = LatestInfo.Version;
        HasUpdate = false;
    }

    /// <summary>打开 release 页（GitHub 或 Gitee，看 LatestInfo.Source）。</summary>
    public void OpenReleasePage()
    {
        if (LatestInfo is null || string.IsNullOrEmpty(LatestInfo.HtmlUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(LatestInfo.HtmlUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UpdateBanner] 打开 release 页失败");
        }
    }
}
